using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Providers.Cli;
using Altim.Providers.Codex.AppServer;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Codex.Rollout;
using Altim.Providers.Codex.State;
using Altim.Providers.Diagnostics;

namespace Altim.Providers.Codex;

/// <summary>
/// The OpenAI Codex integration.
/// </summary>
/// <remarks>
/// <para>
/// Quota comes from the app-server when it can, and from the newest local rollout snapshot
/// when it cannot. Those are not interchangeable and are not presented as if they were: a
/// recovered snapshot is returned with its age stated in
/// <see cref="ProviderUsage.StatusDetail"/>, so a number from four hours ago never sits on
/// screen pretending to be current.
/// </para>
/// <para>
/// <b>A skipped call is not a missing reading.</b> The live call is gated to at most one a
/// minute, and the last successful live reading is kept with its own observation time and
/// keeps answering inside that window. Falling back to the local file whenever the gate was
/// closed made one tick in six live and the rest stale, flipped the status line back and
/// forth, and wrote alternating rows into the history table.
/// </para>
/// <para>
/// The live call is skipped outright when network calls are switched off, when the CLI
/// reports API-key authentication (where it hard-errors), and when the refresh gate says
/// the floor has not elapsed. None of those skips affects the local reads: the rollout tail
/// and the state database are files on this machine and are read on every refresh.
/// </para>
/// <para>
/// <see cref="ProviderUsage.Tokens"/> is <b>always locally observed</b>. The server reports
/// a lifetime grand total with no breakdown behind it, and local sums were measured about
/// 16 per cent away from server accounting, so a field that alternated between the two
/// would move when nothing had happened. The server figure is published separately, as
/// <see cref="ServerReportedUsage"/>, where it can be labelled for what it is.
/// </para>
/// <para>
/// Every reading is <see cref="MetricConfidence.BestEffort"/>. There is no documented
/// interface for an individual ChatGPT-plan account's Codex quota: the platform Usage API
/// is organisation-and-key scoped and Codex Enterprise Analytics is workspace scoped.
/// Calling any of this documented would be a lie about how stable it is.
/// </para>
/// <para>
/// The provider is also an <see cref="IUsageHistorySource"/>. The same app-server exchange
/// that answers the live quota question carries roughly three months of dated daily token
/// totals, and <see cref="GetHistoryAsync"/> hands those back so a freshly installed Altim
/// has a usage map on its first day rather than its ninetieth. It is the same network call,
/// under the same permission and the same gate — and because the gate is usually spent by
/// the live meter, a backfill that found it shut answers from the reading the meter took,
/// rather than reporting nothing and being retried for ever.
/// </para>
/// </remarks>
public sealed class CodexUsageProvider : IUsageProvider, IUsageHistorySource, IDisposable
{
    private readonly CodexOptions _options;
    private readonly ICodexAppServerClient _appServer;
    private readonly CodexDoctorReader _doctor;
    private readonly IProcessMonitor _processMonitor;
    private readonly IRefreshGate? _networkGate;
    private readonly INetworkPolicy? _networkPolicy;
    private readonly string? _home;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProviderUsage _usage;
    private IReadOnlyList<AgentSession> _sessions = [];
    private CodexAuthMode _authMode = CodexAuthMode.Unknown;
    private DateTimeOffset? _authModeCheckedAt;
    private DateTimeOffset? _lastLiveAttemptAt;
    private CodexRateLimitSnapshot? _lastLiveSnapshot;
    private bool _disposed;

    /// <summary>
    /// Creates the provider.
    /// </summary>
    /// <param name="options">What the reader may do. Defaults to <see cref="CodexOptions.Default"/>.</param>
    /// <param name="appServer">The live quota client. Defaults to the real app-server client.</param>
    /// <param name="cliRunner">Runs <c>codex doctor --json</c>. Defaults to the real runner.</param>
    /// <param name="processMonitor">
    /// Detects a running Codex process. Defaults to a scanner that matches the executable
    /// name and reads nothing else about the process.
    /// </param>
    /// <param name="homeOverride">
    /// The Codex home to read, for tests. Defaults to the resolved location.
    /// </param>
    /// <param name="timeProvider">The clock. Defaults to the system clock.</param>
    /// <param name="networkGate">
    /// The floor for the one call that reaches the network. When supplied it owns the
    /// decision; when not, <see cref="CodexOptions.MinimumLiveCallInterval"/> is enforced
    /// here instead. <b>Only</b> the app-server call is gated: the rollout tail and the
    /// state database are local files and are read on every refresh regardless.
    /// </param>
    /// <param name="networkPolicy">
    /// The live answer to "may Altim reach the vendor at all", read at the moment of the
    /// call so that a user switching the setting off stops the next one. When null only
    /// <see cref="CodexOptions.AllowNetworkCalls"/> decides. The two compose: both must
    /// allow.
    /// </param>
    public CodexUsageProvider(
        CodexOptions? options = null,
        ICodexAppServerClient? appServer = null,
        ICliRunner? cliRunner = null,
        IProcessMonitor? processMonitor = null,
        string? homeOverride = null,
        TimeProvider? timeProvider = null,
        IRefreshGate? networkGate = null,
        INetworkPolicy? networkPolicy = null)
    {
        _options = options ?? CodexOptions.Default;
        _appServer = appServer ?? new CodexAppServerClient();
        _doctor = new CodexDoctorReader(cliRunner ?? new CliRunner());
        _processMonitor = processMonitor ?? CreateDefaultProcessScanner();
        _home = homeOverride ?? CodexPaths.ResolveHome();
        _time = timeProvider ?? TimeProvider.System;
        _networkGate = networkGate;
        _networkPolicy = networkPolicy;

        _usage = new ProviderUsage(CodexProviderInfo.Id, ProviderStatus.Unknown, [], null, null, null);
    }

    /// <inheritdoc />
    public event EventHandler<ProviderUsage>? UsageChanged;

    /// <inheritdoc />
    public string Id => CodexProviderInfo.Id;

    /// <inheritdoc />
    public string DisplayName => CodexProviderInfo.DisplayName;

    /// <inheritdoc />
    public ProviderStatus Status => _usage.Status;

    /// <summary>
    /// The account figures the server itself reported, when the live call has produced any.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="ProviderUsage.Tokens"/> on purpose. This is a lifetime
    /// grand total from the provider's own accounting; that is a per-component sum observed
    /// locally, measured about 16 per cent away from it. Presenting them in one field would
    /// mean a number whose meaning changed depending on whether the last call succeeded.
    /// </remarks>
    public CodexAccountUsage? ServerReportedUsage { get; private set; }

    /// <summary>When <see cref="ServerReportedUsage"/> was read.</summary>
    public DateTimeOffset? ServerReportedUsageAt { get; private set; }

    /// <summary>
    /// Whether the live quota call is permitted right now: the constructed option and the
    /// live policy must both allow it.
    /// </summary>
    private bool NetworkCallsAllowed =>
        _options.AllowNetworkCalls && (_networkPolicy?.AllowsNetworkCalls ?? true);

    /// <summary>
    /// A process scanner that finds the Codex CLI by executable name only.
    /// </summary>
    /// <remarks>
    /// The documented signal is "a process named <c>codex</c> with a subcommand in its
    /// arguments". Altim takes the first half and refuses the second: reading another
    /// process's command line is how a monitor ends up holding someone else's API key,
    /// which was observed on the verification machine. The cost is that a <c>codex</c>
    /// helper invocation is indistinguishable from a session here, which is why a detected
    /// process is reported as process presence and never attached to a particular thread.
    /// </remarks>
    public static ProcessScanner CreateDefaultProcessScanner() =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["codex"] = CodexProviderInfo.Id });

    /// <inheritdoc />
    public async ValueTask<ProviderUsage> GetUsageAsync(CancellationToken ct)
    {
        if (_usage.Status is ProviderStatus.Unknown)
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        return _usage;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken ct)
    {
        if (_usage.Status is ProviderStatus.Unknown)
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        return _sessions;
    }

    /// <inheritdoc />
    public async ValueTask RefreshAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ProviderUsage previous = _usage;
            (ProviderUsage usage, IReadOnlyList<AgentSession> sessions) = await ReadAsync(ct).ConfigureAwait(false);

            _usage = usage;
            _sessions = sessions;

            if (!UsageReadings.AreEquivalent(previous, usage))
            {
                UsageChanged?.Invoke(this, usage);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>This is the network call.</b> It is the same <c>account/usage/read</c> exchange
    /// the live quota read makes, and it is held to the same two permissions: the
    /// <see cref="INetworkPolicy"/> the user controls and the <see cref="IRefreshGate"/>
    /// that keeps the CLI from being asked more than once a minute. With permission off
    /// nothing is started — not the app-server and not <c>codex doctor</c> — and the answer
    /// is empty.
    /// </para>
    /// <para>
    /// <b>A call that did not go through falls back to the reading already in hand.</b>
    /// The gate opens once a minute and the live meter takes it within seconds, so a
    /// backfill that treated a closed gate as no answer was measured never to produce one:
    /// the caller does not stamp an empty result, so it asked again every five minutes for
    /// ever and the Codex row stayed unknown. The remembered figures are the provider's
    /// own, bounded by <see cref="CodexOptions.LiveSnapshotRetention"/>, and no day is
    /// invented to fill a gap in them.
    /// </para>
    /// <para>
    /// An empty answer always means "nothing to backfill" and never "nothing was used". A
    /// missing CLI, a shut gate with nothing remembered, network permission switched off, a
    /// refused exchange and a reply whose buckets are in a shape this reader does not
    /// recognise all produce the same empty list, which the map renders as unknown days
    /// rather than as zeroes.
    /// </para>
    /// <para>
    /// Every day returned is <see cref="UsageDaySource.Backfilled"/>, so a day Altim watched
    /// itself is never overwritten by one read back from the provider.
    /// </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<UsageDay>> GetHistoryAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (from > to)
        {
            return [];
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _time.GetUtcNow();

            CodexLiveResult live = await TryLiveAsync(_appServer.IsAvailable, now, ct).ConfigureAwait(false);

            // Decided before the reading is remembered, so that "the live answer wins" is
            // a rule this method applies rather than a side effect of RememberLive having
            // just overwritten the field the fallback reads back.
            CodexAccountUsage? usage = ChooseHistory(live, now);

            // The exchange answers both questions at once. Keeping the quota half means a
            // backfill does not spend the minute's one call and leave the meters stale.
            RememberLive(live, now);

            return ToDays(usage, from, to, now);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private static ProviderUsage NotDetected() =>
        new(CodexProviderInfo.Id, ProviderStatus.NotDetected, [], null, null, "Codex is not installed on this machine");

    /// <summary>
    /// Turns locally observed counts into the contract's token totals.
    /// </summary>
    /// <remarks>
    /// <c>cache_write_input_tokens</c> is part of the real rollout schema and is carried
    /// through. A component the source did not report stays null, because null means "not
    /// reported" and zero would claim the provider measured it and found nothing.
    /// </remarks>
    private static TokenTotals? ToTotals(CodexTokenCounts? counts)
    {
        if (counts is not { HasAny: true } value)
        {
            return null;
        }

        return new TokenTotals(value.Input, value.Output, value.CachedInput, value.CacheWrite);
    }

    /// <summary>
    /// Turns the provider's daily buckets into day rows inside the requested range.
    /// </summary>
    /// <param name="usage">The account figures the exchange returned, if any.</param>
    /// <param name="from">First day, inclusive.</param>
    /// <param name="to">Last day, inclusive.</param>
    /// <param name="now">The instant to stamp the rows with.</param>
    /// <returns>One row per accounted day, oldest first. A day with no bucket is absent.</returns>
    /// <remarks>
    /// <para>
    /// <b>The reply reports one undifferentiated token figure per day.</b> It does not say
    /// how much of that was input, output, cache read or cache write, so the whole figure
    /// goes into <see cref="TokenTotals.Input"/> and the other three components stay
    /// <see langword="null"/> — unreported, which is not zero. Spreading the number across
    /// components would be Altim inventing a breakdown the provider never gave, and the
    /// tooltip would then present that invention as a reading.
    /// </para>
    /// <para>
    /// <see cref="UsageDay.PeakPercent"/> is <see langword="null"/> for every row, because
    /// these buckets are volumes and carry no percentage. The lifetime summary does report a
    /// busiest-day figure, but turning that into a percentage would mean dividing by an
    /// allowance OpenAI does not publish, and the result would be a number Altim computed
    /// sitting in a field that says the provider reported it.
    /// </para>
    /// <para>
    /// A date the reply gives twice is summed: both figures were reported, and dropping one
    /// would quietly lose usage that the provider did account for.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<UsageDay> ToDays(CodexAccountUsage? usage, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        if (usage is null || usage.DailyBuckets.Count == 0)
        {
            return [];
        }

        var totals = new Dictionary<DateOnly, long>();
        foreach (CodexDailyBucket bucket in usage.DailyBuckets)
        {
            if (bucket.Day < from || bucket.Day > to)
            {
                continue;
            }

            totals[bucket.Day] = totals.TryGetValue(bucket.Day, out long running)
                ? running + bucket.Tokens
                : bucket.Tokens;
        }

        var days = new List<UsageDay>(totals.Count);
        foreach (KeyValuePair<DateOnly, long> entry in totals.OrderBy(static pair => pair.Key))
        {
            days.Add(new UsageDay(
                CodexProviderInfo.Id,
                entry.Key,
                new TokenTotals(entry.Value, null, null, null),
                PeakPercent: null,
                UsageDaySource.Backfilled,
                now));
        }

        return days;
    }

    /// <summary>
    /// Adds an optional component to an optional running sum without inventing a zero.
    /// </summary>
    private static long? Accumulate(long? running, long? component) =>
        component is { } value ? (running ?? 0L) + value : running;

    private static CodexTokenCounts Add(CodexTokenCounts running, CodexTokenCounts counts) => new(
        Accumulate(running.Input, counts.Input),
        Accumulate(running.CachedInput, counts.CachedInput),
        Accumulate(running.CacheWrite, counts.CacheWrite),
        Accumulate(running.Output, counts.Output),
        Accumulate(running.ReasoningOutput, counts.ReasoningOutput),
        Accumulate(running.Total, counts.Total));

    private static AgentSession? ToSession(CodexThreadSummary summary, CodexTokenCounts? counts)
    {
        if (summary.ThreadId is not { } id)
        {
            return null;
        }

        // No start instant, no session row. The last-activity instant is not a start time,
        // and neither is the clock: a row that said "started just now" on every refresh
        // would be a number Altim made up.
        if (summary.CreatedAt is not { } startedAt)
        {
            return null;
        }

        return new AgentSession(
            id,
            CodexProviderInfo.Id,
            startedAt,
            summary.UpdatedAt,
            ToTotals(counts),
            summary.ModelId,
            IsActive: false);
    }

    private static bool IsNewer(CodexRateLimitSnapshot candidate, CodexRateLimitSnapshot? incumbent)
    {
        if (incumbent is null)
        {
            return true;
        }

        if (candidate.ObservedAt is not { } candidateAt)
        {
            return false;
        }

        return incumbent.ObservedAt is not { } incumbentAt || candidateAt > incumbentAt;
    }

    private static DateTimeOffset? LastWriteOrNull(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<(ProviderUsage Usage, IReadOnlyList<AgentSession> Sessions)> ReadAsync(CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();

        try
        {
            bool homePresent = _home is not null && Directory.Exists(_home);
            bool cliPresent = _appServer.IsAvailable;

            if (!homePresent && !cliPresent)
            {
                return (NotDetected(), []);
            }

            CodexLocalScan local = ReadLocal();
            IReadOnlyList<DetectedProcess> processes = await ScanProcessesAsync(ct).ConfigureAwait(false);
            bool processRunning = ProcessScanner.HasProcess(processes, CodexProviderInfo.Id);

            CodexLiveResult live = await TryLiveAsync(cliPresent, now, ct).ConfigureAwait(false);
            RememberLive(live, now);

            CodexRateLimitSnapshot? snapshot = Choose(_lastLiveSnapshot, local.Snapshot, now);
            IReadOnlyList<UsageMetric> metrics = snapshot is null
                ? []
                : CodexMetricFactory.Build(snapshot.Windows, MetricConfidence.BestEffort, now);

            TokenTotals? tokens = ToTotals(local.Tokens);
            string? detail = DescribeStatus(live, snapshot, now);
            ProviderStatus status = ResolveStatus(processRunning, local, metrics.Count > 0, now);

            return (new ProviderUsage(CodexProviderInfo.Id, status, metrics, tokens, now, detail), local.Sessions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A provider-level failure is a status, never a crash, and it carries no
            // metrics: an unavailable reading is not a zero. The sentence is the fixed one;
            // exception text names files and would end up on screen.
            return (
                new ProviderUsage(CodexProviderInfo.Id, ProviderStatus.Error, [], null, now, ProviderUsage.UnavailableDetail),
                []);
        }
    }

    /// <summary>
    /// Drops everything the live call produced, so nothing the vendor reported outlives the
    /// permission to ask for it.
    /// </summary>
    private void ForgetLiveReadings()
    {
        _lastLiveSnapshot = null;
        ServerReportedUsage = null;
        ServerReportedUsageAt = null;
    }

    private void RememberLive(CodexLiveResult live, DateTimeOffset now)
    {
        if (live.Outcome is not CodexLiveOutcome.Succeeded)
        {
            return;
        }

        if (live.RateLimits is { } snapshot)
        {
            // Stamped with the caller's clock rather than the client's, so age is measured
            // against the same instant everything else on this reading is.
            _lastLiveSnapshot = snapshot with { ObservedAt = snapshot.ObservedAt ?? now };
        }

        if (live.AccountUsage is { } usage)
        {
            ServerReportedUsage = usage;
            ServerReportedUsageAt = now;
        }
    }

    /// <summary>
    /// Picks between the last live reading and the newest local snapshot.
    /// </summary>
    /// <remarks>
    /// A live reading inside its retention window wins outright: it is what the server said,
    /// and a skipped call does not make it less true. Past that, whichever reading is
    /// actually fresher wins, so a skipped call can never demote a live figure in favour of
    /// an older local one.
    /// </remarks>
    private CodexRateLimitSnapshot? Choose(CodexRateLimitSnapshot? live, CodexRateLimitSnapshot? local, DateTimeOffset now)
    {
        if (live is null)
        {
            return local;
        }

        if (local is null || Age(live, now) <= _options.LiveSnapshotRetention)
        {
            return live;
        }

        return IsNewer(local, live) ? local : live;
    }

    /// <summary>
    /// Picks between the reading this call produced and the one already in hand.
    /// </summary>
    /// <param name="live">How the live attempt ended, and what it carried.</param>
    /// <param name="now">The instant the age of a remembered reading is measured against.</param>
    /// <returns>The account figures to build days from, or <see langword="null"/> for none.</returns>
    /// <remarks>
    /// <para>
    /// <b>A skipped call is not a missing reading</b>, and it is not a missing history
    /// either. The gate opens once a minute and the scheduler's live read takes it within
    /// seconds of it opening, so a backfill asking every five minutes at an arbitrary
    /// instant essentially never finds it open: on the verification machine
    /// <c>maintenance.last_backfill.codex</c> was never written at all, because an empty
    /// answer is not stamped as a run, and the Codex row of the map would have stayed
    /// unknown for as long as the machine kept running.
    /// </para>
    /// <para>
    /// The daily buckets are whole-day figures that barely move minute to minute, so a
    /// reading taken a few minutes ago is a real answer rather than a guess — and it is
    /// the provider's own answer, not an interpolation. Nothing here invents a bucket: a
    /// day the provider did not report is absent from the remembered reading exactly as
    /// it is absent from a fresh one, and stays unknown.
    /// </para>
    /// <para>
    /// The bound is <see cref="CodexOptions.LiveSnapshotRetention"/>, the same one
    /// <see cref="Choose"/> applies to a remembered quota snapshot, so "how long a live
    /// reading keeps answering" means one thing in this file rather than two. It is
    /// longer than the call floor, so one slow or failed call cannot demote a good
    /// reading, and far shorter than a day, so a machine left running for a week never
    /// backfills today's bucket from a reading taken while it was a different day.
    /// </para>
    /// </remarks>
    private CodexAccountUsage? ChooseHistory(CodexLiveResult live, DateTimeOffset now)
    {
        if (live.AccountUsage is { } fresh)
        {
            // What the server has just said wins outright, including when it accounts
            // for fewer days than the reading in hand. The remembered figures are a
            // fallback, never a cache to be merged into an answer.
            return fresh;
        }

        if (ServerReportedUsage is not { } remembered || ServerReportedUsageAt is not { } observed)
        {
            return null;
        }

        return Age(observed, now) <= _options.LiveSnapshotRetention ? remembered : null;
    }

    private static TimeSpan Age(CodexRateLimitSnapshot snapshot, DateTimeOffset now) =>
        Age(snapshot.ObservedAt, now);

    /// <summary>
    /// How old a reading is, or <see cref="TimeSpan.Zero"/> when it does not say when it
    /// was taken or the clock has moved backwards since.
    /// </summary>
    private static TimeSpan Age(DateTimeOffset? observedAt, DateTimeOffset now) =>
        observedAt is { } observed && now > observed ? now - observed : TimeSpan.Zero;

    private async Task<IReadOnlyList<DetectedProcess>> ScanProcessesAsync(CancellationToken ct)
    {
        try
        {
            return await _processMonitor.ScanAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private async Task<CodexLiveResult> TryLiveAsync(bool cliPresent, DateTimeOffset now, CancellationToken ct)
    {
        if (!cliPresent)
        {
            return CodexLiveResult.NotDetected;
        }

        if (!NetworkCallsAllowed)
        {
            // Strict local-only. The remembered live reading came from the vendor, and
            // Choose would go on presenting it for as long as no local snapshot outranked
            // it — which, on a machine with no rollout snapshot at all, is forever. The
            // user asked for local figures, so the server's are forgotten and the meters
            // fall back to what the rollout tail says, or disappear.
            ForgetLiveReadings();
            return CodexLiveResult.Skipped;
        }

        await EnsureAuthModeAsync(now, ct).ConfigureAwait(false);
        if (_authMode is CodexAuthMode.ApiKey or CodexAuthMode.NotAuthenticated)
        {
            return CodexLiveResult.Skipped;
        }

        if (!MayCallNow(now))
        {
            return CodexLiveResult.Skipped;
        }

        return await _appServer.ReadAsync(_options.LiveCallTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the gate — or, when there is none, the local floor — whether the one call that
    /// reaches the network may be made now.
    /// </summary>
    private bool MayCallNow(DateTimeOffset now)
    {
        if (_networkGate is not null)
        {
            return _networkGate.TryAcquire(Id);
        }

        if (_lastLiveAttemptAt is { } last && now - last < _options.MinimumLiveCallInterval)
        {
            return false;
        }

        _lastLiveAttemptAt = now;
        return true;
    }

    private async Task EnsureAuthModeAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_authModeCheckedAt is { } checkedAt && now - checkedAt < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _authModeCheckedAt = now;
        _authMode = await _doctor.ReadAuthModeAsync(_options.DoctorTimeout, ct).ConfigureAwait(false);
    }

    private ProviderStatus ResolveStatus(bool processRunning, CodexLocalScan local, bool hasMetrics, DateTimeOffset now)
    {
        if (processRunning)
        {
            return ProviderStatus.Active;
        }

        if (local.LastActivityAt is { } activity && now - activity <= _options.SessionActivityWindow)
        {
            return ProviderStatus.Active;
        }

        return hasMetrics || local.Sessions.Count > 0 ? ProviderStatus.Idle : ProviderStatus.Detected;
    }

    private string? DescribeStatus(CodexLiveResult live, CodexRateLimitSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot?.Source is CodexSnapshotSource.Live)
        {
            // Inside the call floor the last live reading is the current one. Saying so on
            // one tick and "showing a snapshot from a minute ago" on the next would be a
            // status line that changed while nothing did.
            return Age(snapshot, now) <= _options.MinimumLiveCallInterval
                ? null
                : "Showing the last live reading from " + UsageReadings.DescribeAge(snapshot.ObservedAt, now);
        }

        if (snapshot is null)
        {
            return live.Outcome switch
            {
                CodexLiveOutcome.NotDetected => "Codex CLI not found; no local quota snapshot either",
                CodexLiveOutcome.Skipped when !NetworkCallsAllowed => "Network calls are off and no local quota snapshot was found",
                CodexLiveOutcome.Skipped => "No local quota snapshot found",
                _ => "Live quota unavailable and no local snapshot was found",
            };
        }

        string age = UsageReadings.DescribeAge(snapshot.ObservedAt, now);
        return live.Outcome switch
        {
            CodexLiveOutcome.NotDetected => "Codex CLI not found; showing a local snapshot from " + age,
            CodexLiveOutcome.Skipped when !NetworkCallsAllowed => "Network calls are off; showing a local snapshot from " + age,
            CodexLiveOutcome.Skipped when _authMode is CodexAuthMode.ApiKey =>
                "Live quota is unavailable under API-key authentication; showing a local snapshot from " + age,
            CodexLiveOutcome.Skipped when _authMode is CodexAuthMode.NotAuthenticated =>
                "Codex is not signed in; showing a local snapshot from " + age,
            CodexLiveOutcome.Skipped => "Showing a local snapshot from " + age,
            CodexLiveOutcome.TimedOut => "The live quota call timed out; showing a local snapshot from " + age,
            _ => "The live quota call failed; showing a local snapshot from " + age,
        };
    }

    /// <summary>
    /// Reads the local store: the state database for which files to look at, and the tail of
    /// each of those files for a quota snapshot and the session's cumulative tokens.
    /// </summary>
    /// <remarks>
    /// Token counts are keyed by the rollout file they came from and read back the same way.
    /// Keying them by position in the candidate list and reading them back by position in
    /// the thread list is the same thing only while every thread has a rollout path: one row
    /// without one shifted every later session's tokens onto its neighbour.
    /// </remarks>
    private CodexLocalScan ReadLocal()
    {
        if (_home is null)
        {
            return CodexLocalScan.Empty;
        }

        IReadOnlyList<CodexThreadRow> rows = [];
        string? databasePath = CodexPaths.FindStateDatabase(_home);
        if (databasePath is not null)
        {
            rows = new CodexStateDatabase(databasePath).ReadRecent(_options.MaxSessionsToRead);
        }

        StringComparer pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidates = new List<string>(rows.Count);
        var seen = new HashSet<string>(pathComparer);
        foreach (CodexThreadRow row in rows)
        {
            if (row.RolloutPath is { } path && seen.Add(path))
            {
                candidates.Add(path);
            }
        }

        if (candidates.Count == 0)
        {
            // No state database, or a schema this reader does not understand. Fall back to
            // the newest files by modification time, still bounded and still shallow.
            candidates = [.. CodexPaths.FindRecentRollouts(_home, _options.MaxSessionsToRead)];
        }

        CodexRateLimitSnapshot? best = null;
        CodexTokenCounts summed = default;
        bool sawTokens = false;
        var tokensByPath = new Dictionary<string, CodexTokenCounts>(pathComparer);

        foreach (string path in candidates)
        {
            IReadOnlyList<CodexRolloutRecord> records = CodexRolloutReader.ReadTail(path, _options.RolloutTailBytes);
            if (records.Count == 0)
            {
                continue;
            }

            CodexRateLimitSnapshot? snapshot = CodexRolloutReader.LatestSnapshot(records, LastWriteOrNull(path));
            if (snapshot is not null && IsNewer(snapshot, best))
            {
                best = snapshot;
            }

            // Cumulative totals restate the whole session on every line, so exactly one
            // value per session is taken. Summing the lines would inflate a session by
            // roughly its turn count.
            if (CodexRolloutReader.LatestCumulativeTokens(records) is { } counts)
            {
                sawTokens = true;
                tokensByPath[path] = counts;
                summed = Add(summed, counts);
            }
        }

        var sessions = new List<AgentSession>(rows.Count);
        DateTimeOffset? lastActivity = null;
        foreach (CodexThreadRow row in rows)
        {
            if (row.Summary.UpdatedAt is { } updated && (lastActivity is null || updated > lastActivity))
            {
                lastActivity = updated;
            }

            CodexTokenCounts? counts = row.RolloutPath is { } path && tokensByPath.TryGetValue(path, out CodexTokenCounts found)
                ? found
                : null;

            if (ToSession(row.Summary, counts) is { } session)
            {
                sessions.Add(session);
            }
        }

        return new CodexLocalScan(best, sawTokens ? summed : null, sessions, lastActivity);
    }

    /// <summary>
    /// What one pass over the local store found.
    /// </summary>
    /// <param name="Snapshot">The newest quota snapshot recovered from a rollout tail.</param>
    /// <param name="Tokens">The locally observed token sum across the scanned sessions.</param>
    /// <param name="Sessions">The sessions that reported enough to be described.</param>
    /// <param name="LastActivityAt">
    /// The newest activity instant any thread reported, including threads that carried too
    /// little to become a session row. Status is a statement about the installation, so it
    /// is answered from every row rather than only from the ones that made it into the list.
    /// </param>
    private sealed record CodexLocalScan(
        CodexRateLimitSnapshot? Snapshot,
        CodexTokenCounts? Tokens,
        IReadOnlyList<AgentSession> Sessions,
        DateTimeOffset? LastActivityAt)
    {
        public static CodexLocalScan Empty { get; } = new(null, null, [], null);
    }
}
