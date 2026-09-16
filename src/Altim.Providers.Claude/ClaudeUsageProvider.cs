using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Providers.Claude.Sessions;
using Altim.Providers.Claude.StatusLine;
using Altim.Providers.Claude.Transcripts;
using Altim.Providers.Claude.Usage;
using Altim.Providers.Cli;
using Altim.Providers.Diagnostics;

namespace Altim.Providers.Claude;

/// <summary>
/// The Claude Code integration.
/// </summary>
/// <remarks>
/// <para>
/// Three sources, in order of how much they can be trusted. The status line is documented
/// and is the only local source of reset instants, so it wins whenever it is fresh. The
/// headless usage summary fills the gap when no session has run, and adds the Opus-only and
/// Sonnet-only weekly windows the status line does not expose. Transcripts supply token
/// history and nothing else: they carry no quota figure at all.
/// </para>
/// <para>
/// <b>"Documented" is not the same as "true now."</b> The status line is only written while
/// a session is running, so a Friday file is still sitting there on Monday. It is preferred
/// while it is fresh; once it has gone stale a fresher summary reading takes over, and a
/// stale reading is only shown when there is nothing better, with its age stated. A window
/// whose reset instant has already passed produces no metric at all — the number was true
/// of a window that has ended, and an 85 per cent five-hour meter for a window that reset
/// days ago is worse than no meter.
/// </para>
/// <para>
/// The sources are never blended. A status-line window and a summary window for the same
/// limit are the same number from two places; one of them is used and the other is
/// discarded, rather than averaged into something neither source said.
/// </para>
/// </remarks>
public sealed class ClaudeUsageProvider : IUsageProvider, IDisposable
{
    private const string FiveHourKey = "five_hour";
    private const string SevenDayKey = "seven_day";
    private const string SpendLimitKey = "spend_limit";
    private const string SevenDayOpusKey = "seven_day_opus";
    private const string SevenDaySonnetKey = "seven_day_sonnet";

    /// <summary>
    /// How many sessions' running totals to remember. Past this the least recently seen is
    /// forgotten, so a long-lived process cannot grow this without bound.
    /// </summary>
    private const int MaxRememberedSessions = 512;

    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDayWindow = TimeSpan.FromDays(7);

    private readonly ClaudeOptions _options;
    private readonly ICliRunner _runner;
    private readonly ClaudeAgentsReader _agents;
    private readonly ClaudeTranscriptScanner _transcripts;
    private readonly IProcessMonitor _processMonitor;
    private readonly IReadOnlyList<string> _configRoots;
    private readonly TimeProvider _time;
    private readonly INetworkPolicy? _networkPolicy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _command;

    /// <summary>
    /// Running per-session totals. The scanner returns only what the newly appended bytes
    /// contained, so the figure for a session has to be accumulated here; taking the pass's
    /// own numbers turned every session total into a per-refresh delta after the first
    /// refresh, and a session that had used millions of tokens reported a few thousand.
    /// </summary>
    private readonly Dictionary<string, ClaudeTokenBucket> _sessionTotals = new(StringComparer.Ordinal);
    private readonly Queue<string> _sessionOrder = new();

    private ProviderUsage _usage;
    private IReadOnlyList<AgentSession> _sessions = [];

    /// <summary>
    /// The entries the last listing that actually ran reported, and whether that listing
    /// answered. A skipped listing is no new information, so the last answer stands.
    /// </summary>
    private IReadOnlyList<ClaudeAgentEntry> _lastEntries = [];
    private bool _lastListingAnswered;
    private DateTimeOffset? _agentsListedAt;
    private ClaudeTokenBucket _cumulative;
    private bool _hasCumulative;
    private ClaudeUsageSummary _summary = ClaudeUsageSummary.Empty;
    private DateTimeOffset? _summaryReadAt;
    private ClaudeStatusLineState? _lastStatusLine;
    private bool _disposed;

    /// <summary>
    /// Creates the provider.
    /// </summary>
    /// <param name="options">What the reader may do. Defaults to <see cref="ClaudeOptions.Default"/>.</param>
    /// <param name="cliRunner">Runs the Claude Code CLI. Defaults to the real runner.</param>
    /// <param name="processMonitor">
    /// The fallback session detector. Defaults to a scanner that matches the executable name
    /// and reads nothing else about the process.
    /// </param>
    /// <param name="configRoots">
    /// The config roots to read, for tests. Defaults to the resolved roots.
    /// </param>
    /// <param name="timeProvider">The clock. Defaults to the system clock.</param>
    /// <param name="command">The Claude Code command name or path.</param>
    /// <param name="networkGate">
    /// The floor for the one call that reaches the network, the headless usage summary. When
    /// supplied it owns the decision; when not,
    /// <see cref="ClaudeOptions.MinimumSummaryInterval"/> is enforced here instead. The
    /// status line and the transcripts are local files and are read on every refresh
    /// regardless.
    /// </param>
    /// <param name="networkPolicy">
    /// The live answer to "may Altim reach the vendor at all", read at the moment of the
    /// call so that a user switching the setting off stops the next one. When null only
    /// <see cref="ClaudeOptions.AllowNetworkCalls"/> decides. The two compose: both must
    /// allow.
    /// </param>
    public ClaudeUsageProvider(
        ClaudeOptions? options = null,
        ICliRunner? cliRunner = null,
        IProcessMonitor? processMonitor = null,
        IReadOnlyList<string>? configRoots = null,
        TimeProvider? timeProvider = null,
        string command = "claude",
        IRefreshGate? networkGate = null,
        INetworkPolicy? networkPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        _options = options ?? ClaudeOptions.Default;
        _runner = cliRunner ?? new CliRunner();
        _command = command;
        _agents = new ClaudeAgentsReader(_runner, command);
        _transcripts = new ClaudeTranscriptScanner(_options);
        _processMonitor = processMonitor ?? CreateDefaultProcessScanner();
        _configRoots = configRoots ?? ClaudePaths.ResolveConfigRoots();
        _time = timeProvider ?? TimeProvider.System;
        NetworkGate = networkGate;
        _networkPolicy = networkPolicy;

        _usage = new ProviderUsage(ClaudeProviderInfo.Id, ProviderStatus.Unknown, [], null, null, null);
    }

    /// <inheritdoc />
    public event EventHandler<ProviderUsage>? UsageChanged;

    /// <inheritdoc />
    public string Id => ClaudeProviderInfo.Id;

    /// <inheritdoc />
    public string DisplayName => ClaudeProviderInfo.DisplayName;

    /// <inheritdoc />
    public ProviderStatus Status => _usage.Status;

    /// <summary>The gate the headless summary call goes through, when one was supplied.</summary>
    private IRefreshGate? NetworkGate { get; }

    /// <summary>
    /// Whether the headless usage summary is permitted right now: the constructed option
    /// and the live policy must both allow it.
    /// </summary>
    private bool NetworkCallsAllowed =>
        _options.AllowNetworkCalls && (_networkPolicy?.AllowsNetworkCalls ?? true);

    /// <summary>
    /// A process scanner that finds the Claude Code CLI by executable name only.
    /// </summary>
    /// <remarks>
    /// Helper invocations such as the browser native-messaging host are excluded by name.
    /// A helper that shares the CLI's executable name and differs only in its arguments
    /// cannot be excluded, because arguments are not read: enumerating processes on the
    /// verification machine exposed a third-party tool passing an API key in plaintext in
    /// its own, and a monitor that captured argv would have ingested it.
    /// </remarks>
    public static ProcessScanner CreateDefaultProcessScanner() =>
        new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["claude"] = ClaudeProviderInfo.Id },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "claude-native-host",
                "claude_native_host",
                "claude-browser-helper",
            });

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
        new(ClaudeProviderInfo.Id, ProviderStatus.NotDetected, [], null, null, "Claude Code is not installed on this machine");

    /// <summary>
    /// Adds a metric, unless the source did not report it or the window it describes has
    /// already ended.
    /// </summary>
    /// <remarks>
    /// A reset instant with no window length is still a reset instant. The spend limit is
    /// reported that way — a percentage and a reset, with no period attached — and building
    /// the window only when a length was known threw the reset away, so the one thing the
    /// user wanted from that row never reached the screen. A zero-length window is the
    /// honest encoding of "reset known, period not".
    /// </remarks>
    private static void AddIfReported(
        List<UsageMetric> metrics,
        string key,
        string label,
        double? percent,
        TimeSpan? windowLength,
        DateTimeOffset? resetsAt,
        MetricConfidence confidence,
        DateTimeOffset now)
    {
        if (percent is null && resetsAt is null)
        {
            return;
        }

        if (resetsAt is { } instant && instant <= now)
        {
            // The window this describes is over. Its percentage was true of a period that
            // has ended and says nothing about the one running now.
            return;
        }

        LimitWindow? window = windowLength is { } length
            ? new LimitWindow(length, resetsAt)
            : resetsAt is not null ? new LimitWindow(TimeSpan.Zero, resetsAt) : null;

        metrics.Add(new UsageMetric(key, label, percent, window, confidence));
    }

    private async Task<(ProviderUsage Usage, IReadOnlyList<AgentSession> Sessions)> ReadAsync(CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();

        try
        {
            bool cliPresent = _runner.Exists(_command);
            if (_configRoots.Count == 0 && !cliPresent)
            {
                return (NotDetected(), []);
            }

            ClaudeStatusLineState? statusLine = ReadStatusLine();
            ClaudeTokenHistory history = _transcripts.Scan(_configRoots, now);
            Accumulate(history);

            await RefreshSummaryAsync(cliPresent, now, ct).ConfigureAwait(false);

            bool statusLineFresh = statusLine?.WrittenAt is { } writtenAt && now - writtenAt <= _options.StatusLineFreshWindow;

            var metrics = new List<UsageMetric>();
            AddStatusLineMetrics(metrics, statusLine, statusLineFresh, now);
            AddSummaryMetrics(metrics, now);

            // The liveness scan first, and it is what decides whether the listing runs at
            // all. It reads a process table; the listing starts a process.
            bool agentRunning = ProcessScanner.HasProcess(
                await ScanProcessesAsync(ct).ConfigureAwait(false), ClaudeProviderInfo.Id);

            ClaudeAgentsListing listing = await ListAgentsAsync(cliPresent, agentRunning, now, ct).ConfigureAwait(false);
            RememberListing(listing);

            IReadOnlyList<AgentSession> sessions = BuildSessions(_lastEntries);
            ProviderStatus status = ResolveStatus(agentRunning, metrics.Count > 0);
            string? detail = DescribeStatus(statusLine, statusLineFresh, metrics, cliPresent, now);

            return (
                new ProviderUsage(ClaudeProviderInfo.Id, status, metrics, BuildTotals(), now, detail),
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
                new ProviderUsage(ClaudeProviderInfo.Id, ProviderStatus.Error, [], null, now, ProviderUsage.UnavailableDetail),
                []);
        }
    }

    /// <summary>
    /// Adds the windows the status line reported, when it is the source in force.
    /// </summary>
    /// <remarks>
    /// A stale status line still supplies a window the summary did not, which is why the
    /// summary is consulted first and this fills what is left. The documented source wins
    /// every contest it is fresh for; it does not win one it has been out of for hours.
    /// </remarks>
    private void AddStatusLineMetrics(List<UsageMetric> metrics, ClaudeStatusLineState? statusLine, bool isFresh, DateTimeOffset now)
    {
        if (statusLine is null)
        {
            return;
        }

        MetricConfidence confidence = MetricConfidence.Documented;

        if (isFresh || _summary.SessionUsedPercent is null)
        {
            AddIfReported(metrics, FiveHourKey, "Session", statusLine.FiveHourUsedPercent, FiveHourWindow, statusLine.FiveHourResetsAt, confidence, now);
        }

        if (isFresh || _summary.WeeklyUsedPercent is null)
        {
            AddIfReported(metrics, SevenDayKey, "Weekly", statusLine.SevenDayUsedPercent, SevenDayWindow, statusLine.SevenDayResetsAt, confidence, now);
        }

        // Nothing else reports the spend limit, so a stale reading is the only reading.
        AddIfReported(metrics, SpendLimitKey, "Spend limit", statusLine.SpendLimitUsedPercent, null, statusLine.SpendLimitResetsAt, confidence, now);
    }

    /// <summary>
    /// Adds the windows the headless summary reported, for limits the status line is not
    /// currently answering for.
    /// </summary>
    private void AddSummaryMetrics(List<UsageMetric> metrics, DateTimeOffset now)
    {
        bool hasFiveHour = metrics.Exists(static m => m.Key == FiveHourKey);
        bool hasSevenDay = metrics.Exists(static m => m.Key == SevenDayKey);

        // The summary's own windows are best-effort: they come out of prose, and they carry
        // no reset instant at all, which is why the documented source outranks them while
        // it is current.
        if (!hasFiveHour && _summary.SessionUsedPercent is { } session)
        {
            metrics.Insert(0, new UsageMetric(FiveHourKey, "Session", session, new LimitWindow(FiveHourWindow, null), MetricConfidence.BestEffort));
        }

        if (!hasSevenDay && _summary.WeeklyUsedPercent is { } weekly)
        {
            metrics.Add(new UsageMetric(SevenDayKey, "Weekly", weekly, new LimitWindow(SevenDayWindow, null), MetricConfidence.BestEffort));
        }

        if (_summary.WeeklyOpusUsedPercent is { } opus)
        {
            metrics.Add(new UsageMetric(SevenDayOpusKey, "Weekly (Opus)", opus, new LimitWindow(SevenDayWindow, null), MetricConfidence.BestEffort));
        }

        if (_summary.WeeklySonnetUsedPercent is { } sonnet)
        {
            metrics.Add(new UsageMetric(SevenDaySonnetKey, "Weekly (Sonnet)", sonnet, new LimitWindow(SevenDayWindow, null), MetricConfidence.BestEffort));
        }

        _ = now;
    }

    /// <summary>
    /// Reads the newest status-line state file across the config roots.
    /// </summary>
    /// <remarks>
    /// A file that exists and could not be opened is transient: the helper rewrites it on a
    /// 300-millisecond debounce and a reader lands on the rewrite sooner or later. The last
    /// good reading is kept for those ticks rather than letting the meters blink out and
    /// the status line announce that nothing was found.
    /// </remarks>
    private ClaudeStatusLineState? ReadStatusLine()
    {
        ClaudeStatusLineState? newest = null;
        bool sawTransientFailure = false;

        foreach (string root in _configRoots)
        {
            StatusLineReadOutcome outcome = ClaudeStatusLineReader.TryRead(
                ClaudePaths.StatusLineStateFile(root),
                out ClaudeStatusLineState? state);

            if (outcome is StatusLineReadOutcome.Unreadable)
            {
                sawTransientFailure = true;
                continue;
            }

            if (state is null)
            {
                continue;
            }

            if (newest is null || (state.WrittenAt is { } candidate && (newest.WrittenAt is not { } incumbent || candidate > incumbent)))
            {
                newest = state;
            }
        }

        if (newest is null && sawTransientFailure)
        {
            return _lastStatusLine;
        }

        if (newest is not null)
        {
            _lastStatusLine = newest;
        }

        return newest;
    }

    private void Accumulate(ClaudeTokenHistory history)
    {
        foreach ((string sessionId, ClaudeTokenBucket delta) in history.BySession)
        {
            RememberSession(sessionId, delta);
        }

        if (history.Totals.MessageCount == 0)
        {
            return;
        }

        ClaudeTokenBucket totals = history.Totals;
        _cumulative = Merge(_cumulative, totals);
        _hasCumulative = true;
    }

    private static ClaudeTokenBucket Merge(ClaudeTokenBucket running, ClaudeTokenBucket delta) => new(
        running.Input + delta.Input,
        running.Output + delta.Output,
        running.CacheRead + delta.CacheRead,
        running.CacheCreation5m + delta.CacheCreation5m,
        running.CacheCreation1h + delta.CacheCreation1h,
        running.CacheCreationUnsplit + delta.CacheCreationUnsplit,
        running.MessageCount + delta.MessageCount);

    private void RememberSession(string sessionId, ClaudeTokenBucket delta)
    {
        if (_sessionTotals.TryGetValue(sessionId, out ClaudeTokenBucket running))
        {
            _sessionTotals[sessionId] = Merge(running, delta);
            return;
        }

        _sessionTotals[sessionId] = delta;
        _sessionOrder.Enqueue(sessionId);

        while (_sessionOrder.Count > MaxRememberedSessions)
        {
            _ = _sessionTotals.Remove(_sessionOrder.Dequeue());
        }
    }

    private TokenTotals? BuildTotals()
    {
        if (!_hasCumulative)
        {
            return null;
        }

        // Both cache-creation tiers plus any unsplit remainder. They are summed only at this
        // boundary, where the contract is one cache-write figure; the tiers stay separate in
        // ClaudeTokenHistory, which is what a price calculation needs.
        return new TokenTotals(
            _cumulative.Input,
            _cumulative.Output,
            _cumulative.CacheRead,
            _cumulative.CacheCreationTotal);
    }

    private async Task RefreshSummaryAsync(bool cliPresent, DateTimeOffset now, CancellationToken ct)
    {
        if (!NetworkCallsAllowed)
        {
            // Strict local-only. The summary is the only source for the Opus-only and
            // Sonnet-only weekly windows, and it is a cached answer from a call the user
            // has since forbidden. Keeping it would leave meters on screen that Altim can
            // no longer confirm and will never refresh again, so it is dropped and those
            // windows read as not reported. The status line and the transcripts are local
            // files and are unaffected.
            _summary = ClaudeUsageSummary.Empty;
            _summaryReadAt = null;
            return;
        }

        if (!cliPresent)
        {
            return;
        }

        if (!MayCallNow(now))
        {
            return;
        }

        CliRunResult result = await _runner
            .RunAsync(_command, ["-p", "--output-format", "json", "/usage"], _options.SummaryTimeout, ct)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            // A parse or run failure leaves the previous summary in place rather than
            // blanking it: the last real reading is more honest than nothing, and it is
            // labelled best-effort either way.
            return;
        }

        ClaudeUsageSummary parsed = ClaudeUsageSummaryParser.ParseEnvelope(result.StandardOutput);
        if (parsed.HasAny)
        {
            _summary = parsed;
        }
    }

    /// <summary>
    /// Asks the gate — or, when there is none, the local floor — whether the one call that
    /// reaches the network may be made now.
    /// </summary>
    private bool MayCallNow(DateTimeOffset now)
    {
        if (NetworkGate is not null)
        {
            return NetworkGate.TryAcquire(Id);
        }

        if (_summaryReadAt is { } last && now - last < _options.MinimumSummaryInterval)
        {
            return false;
        }

        _summaryReadAt = now;
        return true;
    }

    /// <summary>
    /// Runs <c>claude agents --json</c>, unless the last listing is recent enough to stand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one part of a Claude refresh that starts a process, and it used to run on
    /// every refresh. It is also, by a wide margin, the most expensive thing Altim does:
    /// measured on the verification machine, one invocation starts 105 processes and spends
    /// 5.6 seconds of CPU, and takes longer than its own timeout to answer. Refreshes are
    /// event-driven, so tying it to one meant a session writing transcripts continuously
    /// produced 173 child processes a minute and about 17.8% of one core — none of which
    /// appeared in a CPU figure measured from the Altim process alone.
    /// </para>
    /// <para>
    /// So it runs on a floor of its own, at the polling floor, and two things push it out to
    /// the backoff: a scan that cannot see anything that looks like an agent, and a previous
    /// attempt that did not answer. Neither costs the user a status — whether an agent is
    /// running comes from the scan on every refresh — and what is delayed is how soon a new
    /// session appears by name in the activity list.
    /// </para>
    /// </remarks>
    private async Task<ClaudeAgentsListing> ListAgentsAsync(
        bool cliPresent, bool agentRunning, DateTimeOffset now, CancellationToken ct)
    {
        if (!cliPresent)
        {
            return ClaudeAgentsListing.NotDetected;
        }

        if (_agentsListedAt is { } last)
        {
            TimeSpan floor = agentRunning && _lastListingAnswered
                ? _options.AgentsInterval
                : _options.AgentsBackoffInterval;

            if (now >= last && now - last < floor)
            {
                return ClaudeAgentsListing.Skipped;
            }
        }

        _agentsListedAt = now;
        return await _agents.ListAsync(_options.AgentsTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps whatever the last listing that actually ran said.
    /// </summary>
    /// <param name="listing">The listing this refresh produced.</param>
    /// <remarks>
    /// A skipped listing changes nothing: it is not evidence that no session is running, and
    /// treating it as one would blink the activity list out between listings. A failed or
    /// absent one is different — it means the command cannot answer, so the remembered
    /// entries are dropped and the process scan becomes the fallback it has always been.
    /// </remarks>
    private void RememberListing(ClaudeAgentsListing listing)
    {
        switch (listing.Outcome)
        {
            case ClaudeAgentsOutcome.Listed:
                _lastEntries = listing.Entries;
                _lastListingAnswered = true;
                break;

            case ClaudeAgentsOutcome.Skipped:
                break;

            default:
                _lastEntries = [];
                _lastListingAnswered = false;
                break;
        }
    }

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
    /// Turns the entries in force into session rows, each carrying that session's running
    /// token total.
    /// </summary>
    /// <remarks>
    /// An entry with no start instant produces no row. The clock is not a start time: an
    /// entry stamped with "now" on every refresh would show a session that had been running
    /// for hours as having started seconds ago, and would say so again a minute later. The
    /// entry still counts towards the provider's status, because the command listing it is
    /// what says an agent is running.
    /// </remarks>
    private IReadOnlyList<AgentSession> BuildSessions(IReadOnlyList<ClaudeAgentEntry> entries)
    {
        var sessions = new List<AgentSession>(entries.Count);

        foreach (ClaudeAgentEntry entry in entries)
        {
            if (entry.SessionId is not { } sessionId || entry.StartedAt is not { } startedAt)
            {
                continue;
            }

            TokenTotals? tokens = _sessionTotals.TryGetValue(sessionId, out ClaudeTokenBucket bucket)
                ? new TokenTotals(bucket.Input, bucket.Output, bucket.CacheRead, bucket.CacheCreationTotal)
                : null;

            sessions.Add(new AgentSession(
                sessionId,
                ClaudeProviderInfo.Id,
                startedAt,
                null,
                tokens,
                null,
                IsActive: true));
        }

        return sessions;
    }

    private ProviderStatus ResolveStatus(bool agentRunning, bool hasMetrics)
    {
        if (_lastEntries.Count > 0)
        {
            return ProviderStatus.Active;
        }

        if (_lastListingAnswered)
        {
            // The agents listing answered and said nothing is running. It resolves liveness
            // itself — it correctly omitted a registry entry whose process id had been
            // recycled to an unrelated program — so process enumeration does not get to
            // overrule it.
            return hasMetrics ? ProviderStatus.Idle : ProviderStatus.Detected;
        }

        // The listing could not be had at all. That is not "nothing is running", so the
        // fallback applies: the scan that was taken before the listing was decided on.
        return agentRunning
            ? ProviderStatus.Active
            : hasMetrics ? ProviderStatus.Idle : ProviderStatus.Detected;
    }

    private string? DescribeStatus(
        ClaudeStatusLineState? statusLine,
        bool statusLineFresh,
        IReadOnlyList<UsageMetric> metrics,
        bool cliPresent,
        DateTimeOffset now)
    {
        if (metrics.Count == 0)
        {
            if (statusLine is null)
            {
                return cliPresent
                    ? "No quota reading yet. The status line is not installed, or no session has run."
                    : "Claude Code CLI not found; no quota reading available";
            }

            return "The status line reported no rate-limit windows";
        }

        bool showingStatusLine = metrics.Any(static m => m.Confidence is MetricConfidence.Documented);
        if (statusLine is not null && !statusLineFresh && showingStatusLine)
        {
            return "Showing the last status-line reading from " + UsageReadings.DescribeAge(statusLine.WrittenAt, now);
        }

        return null;
    }
}
