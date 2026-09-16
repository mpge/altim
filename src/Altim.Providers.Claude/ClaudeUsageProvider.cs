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
/// The sources are never blended. A status-line window and a summary window for the same
/// limit are the same number from two places; the documented one is used and the other is
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

    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDayWindow = TimeSpan.FromDays(7);

    private readonly ClaudeOptions _options;
    private readonly ICliRunner _runner;
    private readonly ClaudeAgentsReader _agents;
    private readonly ClaudeTranscriptScanner _transcripts;
    private readonly IProcessMonitor _processMonitor;
    private readonly IReadOnlyList<string> _configRoots;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _command;

    private ProviderUsage _usage;
    private IReadOnlyList<AgentSession> _sessions = [];
    private ClaudeTokenBucket _cumulative;
    private bool _hasCumulative;
    private ClaudeUsageSummary _summary = ClaudeUsageSummary.Empty;
    private DateTimeOffset? _summaryReadAt;
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
    public ClaudeUsageProvider(
        ClaudeOptions? options = null,
        ICliRunner? cliRunner = null,
        IProcessMonitor? processMonitor = null,
        IReadOnlyList<string>? configRoots = null,
        TimeProvider? timeProvider = null,
        string command = "claude")
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

            var metrics = new List<UsageMetric>();
            bool statusLineFresh = statusLine?.WrittenAt is { } writtenAt && now - writtenAt <= _options.StatusLineFreshWindow;

            AddStatusLineMetrics(metrics, statusLine);
            AddSummaryMetrics(metrics, statusLine);

            IReadOnlyList<AgentSession> sessions = await ReadSessionsAsync(history, cliPresent, now, ct).ConfigureAwait(false);
            ProviderStatus status = await ResolveStatusAsync(sessions, cliPresent, metrics.Count > 0, ct).ConfigureAwait(false);
            string? detail = DescribeStatus(statusLine, statusLineFresh, metrics.Count, cliPresent, now);

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
            return (
                new ProviderUsage(ClaudeProviderInfo.Id, ProviderStatus.Error, [], null, now, "Unable to retrieve usage"),
                []);
        }
    }

    private static ProviderUsage NotDetected() =>
        new(ClaudeProviderInfo.Id, ProviderStatus.NotDetected, [], null, null, "Claude Code is not installed on this machine");

    private static void AddStatusLineMetrics(List<UsageMetric> metrics, ClaudeStatusLineState? statusLine)
    {
        if (statusLine is null)
        {
            return;
        }

        // A window missing from the payload means no data. It is dropped once its reset
        // passes, so no metric is emitted and the meter disappears rather than reading zero.
        AddIfReported(metrics, FiveHourKey, "Session", statusLine.FiveHourUsedPercent, FiveHourWindow, statusLine.FiveHourResetsAt, MetricConfidence.Documented);
        AddIfReported(metrics, SevenDayKey, "Weekly", statusLine.SevenDayUsedPercent, SevenDayWindow, statusLine.SevenDayResetsAt, MetricConfidence.Documented);
        AddIfReported(metrics, SpendLimitKey, "Spend limit", statusLine.SpendLimitUsedPercent, null, statusLine.SpendLimitResetsAt, MetricConfidence.Documented);
    }

    private static void AddIfReported(
        List<UsageMetric> metrics,
        string key,
        string label,
        double? percent,
        TimeSpan? windowLength,
        DateTimeOffset? resetsAt,
        MetricConfidence confidence)
    {
        if (percent is null && resetsAt is null)
        {
            return;
        }

        LimitWindow? window = windowLength is { } length ? new LimitWindow(length, resetsAt) : null;
        metrics.Add(new UsageMetric(key, label, percent, window, confidence));
    }

    private void AddSummaryMetrics(List<UsageMetric> metrics, ClaudeStatusLineState? statusLine)
    {
        bool hasFiveHour = statusLine?.FiveHourUsedPercent is not null;
        bool hasSevenDay = statusLine?.SevenDayUsedPercent is not null;

        // The summary's own windows are best-effort: they come out of prose. They fill gaps
        // the documented source left, and never displace it.
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
    }

    private ClaudeStatusLineState? ReadStatusLine()
    {
        ClaudeStatusLineState? newest = null;

        foreach (string root in _configRoots)
        {
            ClaudeStatusLineState? state = ClaudeStatusLineReader.Read(ClaudePaths.StatusLineStateFile(root));
            if (state is null)
            {
                continue;
            }

            if (newest is null || (state.WrittenAt is { } candidate && (newest.WrittenAt is not { } incumbent || candidate > incumbent)))
            {
                newest = state;
            }
        }

        return newest;
    }

    private void Accumulate(ClaudeTokenHistory history)
    {
        if (history.Totals.MessageCount == 0)
        {
            return;
        }

        ClaudeTokenBucket delta = history.Totals;
        _cumulative = new ClaudeTokenBucket(
            _cumulative.Input + delta.Input,
            _cumulative.Output + delta.Output,
            _cumulative.CacheRead + delta.CacheRead,
            _cumulative.CacheCreation5m + delta.CacheCreation5m,
            _cumulative.CacheCreation1h + delta.CacheCreation1h,
            _cumulative.CacheCreationUnsplit + delta.CacheCreationUnsplit,
            _cumulative.MessageCount + delta.MessageCount);
        _hasCumulative = true;
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
        if (!cliPresent || !_options.AllowNetworkCalls)
        {
            return;
        }

        if (_summaryReadAt is { } last && now - last < _options.MinimumSummaryInterval)
        {
            return;
        }

        _summaryReadAt = now;

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

    private async Task<IReadOnlyList<AgentSession>> ReadSessionsAsync(
        ClaudeTokenHistory history,
        bool cliPresent,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!cliPresent)
        {
            return [];
        }

        IReadOnlyList<ClaudeAgentEntry> entries = await _agents.ListAsync(_options.AgentsTimeout, ct).ConfigureAwait(false);
        var sessions = new List<AgentSession>(entries.Count);

        foreach (ClaudeAgentEntry entry in entries)
        {
            if (entry.SessionId is not { } sessionId)
            {
                continue;
            }

            TokenTotals? tokens = history.BySession.TryGetValue(sessionId, out ClaudeTokenBucket bucket)
                ? new TokenTotals(bucket.Input, bucket.Output, bucket.CacheRead, bucket.CacheCreationTotal)
                : null;

            sessions.Add(new AgentSession(
                sessionId,
                ClaudeProviderInfo.Id,
                entry.StartedAt ?? now,
                null,
                tokens,
                null,
                IsActive: true));
        }

        return sessions;
    }

    private async Task<ProviderStatus> ResolveStatusAsync(
        IReadOnlyList<AgentSession> sessions,
        bool cliPresent,
        bool hasMetrics,
        CancellationToken ct)
    {
        if (sessions.Count > 0)
        {
            return ProviderStatus.Active;
        }

        if (cliPresent)
        {
            // The agents listing is authoritative and said nothing is running. Process
            // enumeration is only consulted when the listing could not be had at all.
            return hasMetrics ? ProviderStatus.Idle : ProviderStatus.Detected;
        }

        try
        {
            IReadOnlyList<DetectedProcess> processes = await _processMonitor.ScanAsync(ct).ConfigureAwait(false);
            if (ProcessScanner.HasProcess(processes, ClaudeProviderInfo.Id))
            {
                return ProviderStatus.Active;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // A denied process table is the normal case, not a failure.
        }

        return hasMetrics ? ProviderStatus.Idle : ProviderStatus.Detected;
    }

    private string? DescribeStatus(
        ClaudeStatusLineState? statusLine,
        bool statusLineFresh,
        int metricCount,
        bool cliPresent,
        DateTimeOffset now)
    {
        if (metricCount == 0)
        {
            if (statusLine is null)
            {
                return cliPresent
                    ? "No quota reading yet. The status line is not installed, or no session has run."
                    : "Claude Code CLI not found; no quota reading available";
            }

            return "The status line reported no rate-limit windows";
        }

        if (statusLine is not null && !statusLineFresh)
        {
            return "Showing the last status-line reading from " + UsageReadings.DescribeAge(statusLine.WrittenAt, now);
        }

        return null;
    }
}
