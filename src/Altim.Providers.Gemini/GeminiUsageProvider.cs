using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Providers.Cli;
using Altim.Providers.Diagnostics;
using Altim.Providers.Gemini.Sessions;

namespace Altim.Providers.Gemini;

/// <summary>
/// The Gemini CLI integration.
/// </summary>
/// <remarks>
/// <para>
/// <b>This provider reports no percentage, no window and no reset time, and it never will
/// from a local read.</b> Gemini CLI does hold a real remaining-quota figure — per model,
/// with the server's own reset instant — but it fetches it from Google over the network into
/// the memory of a running <c>gemini</c> process and writes it nowhere. Nothing on this
/// machine carries it, so every one of those metrics is unavailable and is rendered as "not
/// reported by this provider" rather than as a zero or an estimate.
/// </para>
/// <para>
/// <b>The published allowance cannot stand in for one either.</b> Google publishes Gemini
/// CLI's free tier as a number of <em>model requests per user per day</em>, not tokens, and
/// the figure differs by subscription — 1,000, 1,500 or 2,000 a day depending on a tier that
/// is only knowable from the account files this reader refuses to open. A percentage made
/// from a local message count against a guessed denominator would be two inventions stacked
/// on one another, which is exactly what `AGENTS.md` rules 1 and 2 forbid. It is not
/// computed. See <c>PROVIDERS.md</c> for the whole trace.
/// </para>
/// <para>
/// What is genuinely there is token history. Every model turn Gemini CLI records carries the
/// response's own <c>usageMetadata</c> counts, each with its own timestamp, which is what
/// makes this provider a real <see cref="IUsageHistorySource"/> rather than a tidy one: the
/// per-day figures come from the messages' own dates and not from anything Altim inferred.
/// </para>
/// <para>
/// Nothing here starts a process or makes a network call. Presence is decided by looking for
/// the executable on the path and for the session store on disk, both of which are file
/// checks, so this provider has no rate-limit gate and no network permission to honour.
/// </para>
/// </remarks>
public sealed class GeminiUsageProvider : IUsageProvider, IUsageHistorySource, IDisposable
{
    private readonly GeminiOptions _options;
    private readonly ICliRunner _runner;
    private readonly GeminiSessionScanner _scanner;
    private readonly IProcessMonitor _processMonitor;
    private readonly string? _home;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _command;

    /// <summary>
    /// Running per-session state. A scan reports only what the newly appended bytes
    /// contained, so a session's figures have to be accumulated here; taking a pass's own
    /// numbers would turn every session total into a per-refresh delta after the first
    /// refresh.
    /// </summary>
    private readonly Dictionary<string, GeminiSessionState> _sessionStates = new(StringComparer.Ordinal);
    private readonly Queue<string> _sessionOrder = new();

    /// <summary>
    /// Running per-day totals, for the same reason: a scan reports only the bytes it has just
    /// read, so a day has to be accumulated across every pass that touched it.
    /// </summary>
    private readonly Dictionary<DateOnly, GeminiTokenBucket> _dayTotals = [];

    private ProviderUsage _usage;
    private IReadOnlyList<AgentSession> _sessions = [];
    private GeminiTokenBucket _cumulative;
    private bool _hasCumulative;
    private bool _disposed;

    /// <summary>
    /// Creates the provider.
    /// </summary>
    /// <param name="options">What the reader may do. Defaults to <see cref="GeminiOptions.Default"/>.</param>
    /// <param name="cliRunner">
    /// Decides whether the CLI is installed. Only <see cref="ICliRunner.Exists"/> is used:
    /// this provider never runs the Gemini CLI. Nothing the CLI can be asked reports quota
    /// without spending a model request, and a passive monitor must not spend a user's
    /// allowance to find out how much of it is left.
    /// </param>
    /// <param name="processMonitor">
    /// Detects a running Gemini process. Defaults to a scanner that matches the executable
    /// name and reads nothing else about the process.
    /// </param>
    /// <param name="homeOverride">
    /// The Gemini home to read, for tests. Defaults to the resolved location.
    /// </param>
    /// <param name="timeProvider">The clock. Defaults to the system clock.</param>
    /// <param name="command">The Gemini CLI command name or path.</param>
    public GeminiUsageProvider(
        GeminiOptions? options = null,
        ICliRunner? cliRunner = null,
        IProcessMonitor? processMonitor = null,
        string? homeOverride = null,
        TimeProvider? timeProvider = null,
        string command = "gemini")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        _options = options ?? GeminiOptions.Default;
        _runner = cliRunner ?? new CliRunner();
        _command = command;
        _scanner = new GeminiSessionScanner(_options);
        _processMonitor = processMonitor ?? CreateDefaultProcessScanner();
        _home = homeOverride ?? GeminiPaths.ResolveHome();
        _time = timeProvider ?? TimeProvider.System;

        _usage = new ProviderUsage(GeminiProviderInfo.Id, ProviderStatus.Unknown, [], null, null, null);
    }

    /// <inheritdoc />
    public event EventHandler<ProviderUsage>? UsageChanged;

    /// <inheritdoc />
    public string Id => GeminiProviderInfo.Id;

    /// <inheritdoc />
    public string DisplayName => GeminiProviderInfo.DisplayName;

    /// <inheritdoc />
    public ProviderStatus Status => _usage.Status;

    /// <summary>
    /// A process scanner that finds the Gemini CLI by executable name only.
    /// </summary>
    /// <remarks>
    /// Arguments are not read, here or anywhere else in this solution: enumerating processes
    /// on the verification machine exposed a third-party tool passing an API key in plaintext
    /// in its own. The cost is the usual one, plus a Gemini-specific one — an npm shim
    /// install runs the CLI as <c>node</c>, and a session started that way is invisible to
    /// this scan. That is why a recently written session file also counts as activity.
    /// </remarks>
    public static ProcessScanner CreateDefaultProcessScanner() =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["gemini"] = GeminiProviderInfo.Id });

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
    /// Session transcripts are the only Gemini source that reaches back before Altim was
    /// installed, and they carry token counts and nothing else. No line in one reports a
    /// quota, so every day here has a null <see cref="UsageDay.PeakPercent"/> — not a zero,
    /// which would claim the user touched none of their allowance that day.
    /// </para>
    /// <para>
    /// A day the scan cannot account for is absent from the result rather than present with
    /// zeroes. Absent is unknown; zero is a day that used nothing, and the map draws them
    /// differently. What reaches the days is six running counts and a date: nothing built
    /// from a path, a project, a prompt or a workspace directory can get this far, because a
    /// <see cref="GeminiTokenBucket"/> has nowhere to put one.
    /// </para>
    /// <para>
    /// Figures are locally observed and are a floor. How far back they reach is the store's
    /// rather than the account's: Gemini CLI's own cleanup is documented to keep 30 days, it
    /// runs only for projects the user opens, and it is driven by a setting rather than a
    /// guarantee — so the reach is whatever happens to still be on disk inside this reader's
    /// own window.
    /// </para>
    /// <para>
    /// It costs a session scan and nothing else: no process is started and no network call is
    /// made. The incremental scanner is shared with the ordinary refresh, so whichever of the
    /// two runs first pays for the new bytes and the other reads them for free.
    /// </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<UsageDay>> GetHistoryAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (to < from)
        {
            return [];
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The same gate the refresh takes, because the scanner is not thread-safe and
            // both callers advance the same file cursors.
            Accumulate(_scanner.Scan(_home, _time.GetUtcNow()));
            return BuildDays(from, to);
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
        new(GeminiProviderInfo.Id, ProviderStatus.NotDetected, [], null, null, "Gemini CLI is not installed on this machine");

    private async Task<(ProviderUsage Usage, IReadOnlyList<AgentSession> Sessions)> ReadAsync(CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();

        try
        {
            bool homePresent = _home is not null && Directory.Exists(_home);
            bool cliPresent = _runner.Exists(_command);

            if (!homePresent && !cliPresent)
            {
                return (NotDetected(), []);
            }

            if (homePresent)
            {
                Accumulate(_scanner.Scan(_home, now));
            }

            bool processRunning = ProcessScanner.HasProcess(
                await ScanProcessesAsync(ct).ConfigureAwait(false), GeminiProviderInfo.Id);

            IReadOnlyList<AgentSession> sessions = BuildSessions();
            ProviderStatus status = ResolveStatus(processRunning, now);
            string? detail = DescribeStatus(homePresent, cliPresent);

            // The metric list is empty and is meant to be. Nothing local reports a Gemini
            // percentage, window or reset instant, so there is no metric to publish and the
            // interface says so in its own words rather than showing a zeroed meter.
            return (
                new ProviderUsage(GeminiProviderInfo.Id, status, [], BuildTotals(), now, detail),
                sessions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The sentence is the fixed one. Exception text names files and would end up on
            // screen.
            return (
                new ProviderUsage(GeminiProviderInfo.Id, ProviderStatus.Error, [], null, now, ProviderUsage.UnavailableDetail),
                []);
        }
    }

    private void Accumulate(GeminiTokenHistory history)
    {
        foreach (GeminiSessionRecord record in history.Sessions)
        {
            GeminiTokenBucket delta = history.BySession.TryGetValue(record.SessionId, out GeminiTokenBucket bucket)
                ? bucket
                : default;

            RememberSession(record, delta);
        }

        foreach ((DateOnly day, GeminiTokenBucket delta) in history.ByDay)
        {
            _dayTotals[day] = _dayTotals.TryGetValue(day, out GeminiTokenBucket running)
                ? running.Add(delta)
                : delta;
        }

        if (history.Totals.MessageCount == 0)
        {
            return;
        }

        _cumulative = _cumulative.Add(history.Totals);
        _hasCumulative = true;
    }

    private void RememberSession(GeminiSessionRecord record, GeminiTokenBucket delta)
    {
        if (_sessionStates.TryGetValue(record.SessionId, out GeminiSessionState running))
        {
            _sessionStates[record.SessionId] = running.Merge(record, delta);
            return;
        }

        _sessionStates[record.SessionId] = default(GeminiSessionState).Merge(record, delta);
        _sessionOrder.Enqueue(record.SessionId);

        while (_sessionOrder.Count > _options.MaxRememberedSessions)
        {
            _ = _sessionStates.Remove(_sessionOrder.Dequeue());
        }
    }

    /// <summary>
    /// Turns the accumulated day buckets that fall inside a range into rows, oldest first.
    /// </summary>
    /// <param name="from">First day, inclusive.</param>
    /// <param name="to">Last day, inclusive.</param>
    /// <remarks>
    /// A day with no bucket produces no row. The gap is the answer: the scan has nothing to
    /// say about that day, and a row of zeroes would say it had a quiet one.
    /// </remarks>
    private IReadOnlyList<UsageDay> BuildDays(DateOnly from, DateOnly to)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var days = new List<UsageDay>();

        foreach ((DateOnly day, GeminiTokenBucket bucket) in _dayTotals)
        {
            if (day < from || day > to)
            {
                continue;
            }

            days.Add(new UsageDay(
                GeminiProviderInfo.Id,
                day,
                bucket.ToTotals(),
                PeakPercent: null,
                UsageDaySource.Backfilled,
                now));
        }

        days.Sort(static (a, b) => a.Day.CompareTo(b.Day));
        return days;
    }

    private TokenTotals? BuildTotals() => _hasCumulative ? _cumulative.ToTotals() : null;

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
            // A denied process table is the normal case, not a failure.
            return [];
        }
    }

    /// <summary>
    /// Turns the remembered sessions into rows, newest activity first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session with no start instant produces no row. The clock is not a start time: a row
    /// stamped with "now" on every refresh would show a session that had been running for
    /// hours as having started seconds ago, and would say so again a minute later.
    /// </para>
    /// <para>
    /// <b>No row ever claims to be the running one.</b> Gemini CLI has no listing command
    /// that could resolve liveness the way Claude Code's does, and a detected process cannot
    /// be attached to a particular session without reading its command line, which nothing
    /// in this solution does. Process presence is therefore reported as the provider's
    /// status and never as a property of one session, which is the same position the Codex
    /// reader takes for the same reason.
    /// </para>
    /// </remarks>
    private IReadOnlyList<AgentSession> BuildSessions()
    {
        var sessions = new List<AgentSession>(_sessionStates.Count);

        foreach ((string sessionId, GeminiSessionState state) in _sessionStates)
        {
            if (state.StartedAt is not { } startedAt)
            {
                continue;
            }

            sessions.Add(new AgentSession(
                sessionId,
                GeminiProviderInfo.Id,
                startedAt,
                state.LastActivityAt,
                state.HasTokens ? state.Tokens.ToTotals() : null,
                state.ModelId,
                IsActive: false));
        }

        sessions.Sort(static (a, b) => Compare(b, a));
        return sessions;
    }

    private static int Compare(AgentSession a, AgentSession b)
    {
        DateTimeOffset left = a.LastActivityAt ?? a.StartedAt;
        DateTimeOffset right = b.LastActivityAt ?? b.StartedAt;
        return left.CompareTo(right);
    }

    private bool IsRecent(DateTimeOffset? instant, DateTimeOffset now) =>
        instant is { } value && now >= value && now - value <= _options.SessionActivityWindow;

    /// <summary>
    /// Whether a Gemini session is running, from the two signals available.
    /// </summary>
    /// <remarks>
    /// Either signal is enough, which is the rule the Codex reader already follows. A process
    /// of the right name is the CLI running; a session file written inside the activity
    /// window is a session that was running moments ago. Requiring both would report idle
    /// through an npm-shim install, where the CLI runs as <c>node</c> and the process scan
    /// sees nothing at all.
    /// </remarks>
    private ProviderStatus ResolveStatus(bool processRunning, DateTimeOffset now)
    {
        if (processRunning)
        {
            return ProviderStatus.Active;
        }

        foreach (GeminiSessionState state in _sessionStates.Values)
        {
            if (IsRecent(state.LastActivityAt, now))
            {
                return ProviderStatus.Active;
            }
        }

        return _hasCumulative || _sessionStates.Count > 0 ? ProviderStatus.Idle : ProviderStatus.Detected;
    }

    /// <summary>
    /// Says why there are no meters, every time, because there will never be any.
    /// </summary>
    /// <remarks>
    /// The interface already renders an absent metric as "not reported by this provider".
    /// This is the other half of that sentence: not reported by whom, and why. It is a
    /// constant for a given installation state rather than something that moves, so it does
    /// not churn the reading or the history table.
    /// </remarks>
    private static string DescribeStatus(bool homePresent, bool cliPresent)
    {
        if (!homePresent)
        {
            return "Gemini CLI found; it has written no session store on this machine yet";
        }

        // Both sentences make the same point, because it is the point either way: the
        // absent meters are not a consequence of the CLI being missing, and a user who
        // reinstalled it would get no meters either.
        return cliPresent
            ? "Gemini CLI does not report quota or reset times locally; token history only"
            : "Gemini CLI not found; its session store reports tokens only, never quota";
    }

    /// <summary>
    /// One session's running state, accumulated across passes.
    /// </summary>
    private readonly record struct GeminiSessionState(
        DateTimeOffset? StartedAt,
        DateTimeOffset? LastActivityAt,
        string? ModelId,
        GeminiTokenBucket Tokens,
        bool HasTokens)
    {
        public GeminiSessionState Merge(GeminiSessionRecord record, GeminiTokenBucket delta) => new(
            // The first start instant seen wins: the metadata line is written once and a
            // later pass that does not re-read it must not blank it.
            StartedAt ?? record.StartedAt,
            Later(LastActivityAt, record.LastActivityAt),
            record.ModelId ?? ModelId,
            Tokens.Add(delta),
            HasTokens || delta.MessageCount > 0);

        private static DateTimeOffset? Later(DateTimeOffset? current, DateTimeOffset? candidate)
        {
            if (candidate is not { } value)
            {
                return current;
            }

            return current is { } incumbent && incumbent >= value ? incumbent : value;
        }
    }
}
