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
/// The live call is skipped outright when network calls are switched off, when the CLI
/// reports API-key authentication (where it hard-errors), and when the last call was less
/// than a minute ago.
/// </para>
/// <para>
/// Every reading is <see cref="MetricConfidence.BestEffort"/>. There is no documented
/// interface for an individual ChatGPT-plan account's Codex quota: the platform Usage API
/// is organisation-and-key scoped and Codex Enterprise Analytics is workspace scoped.
/// Calling any of this documented would be a lie about how stable it is.
/// </para>
/// </remarks>
public sealed class CodexUsageProvider : IUsageProvider, IDisposable
{
    private readonly CodexOptions _options;
    private readonly ICodexAppServerClient _appServer;
    private readonly CodexDoctorReader _doctor;
    private readonly IProcessMonitor _processMonitor;
    private readonly string? _home;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProviderUsage _usage;
    private IReadOnlyList<AgentSession> _sessions = [];
    private CodexAuthMode _authMode = CodexAuthMode.Unknown;
    private DateTimeOffset? _authModeCheckedAt;
    private DateTimeOffset? _lastLiveAttemptAt;
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
    public CodexUsageProvider(
        CodexOptions? options = null,
        ICodexAppServerClient? appServer = null,
        ICliRunner? cliRunner = null,
        IProcessMonitor? processMonitor = null,
        string? homeOverride = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? CodexOptions.Default;
        _appServer = appServer ?? new CodexAppServerClient();
        _doctor = new CodexDoctorReader(cliRunner ?? new CliRunner());
        _processMonitor = processMonitor ?? CreateDefaultProcessScanner();
        _home = homeOverride ?? CodexPaths.ResolveHome();
        _time = timeProvider ?? TimeProvider.System;

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
    /// A process scanner that finds the Codex CLI by executable name only.
    /// </summary>
    /// <remarks>
    /// The documented signal is "a process named <c>codex</c> with a subcommand in its
    /// arguments". Altim takes the first half and refuses the second: reading another
    /// process's command line is how a monitor ends up holding someone else's API key,
    /// which was observed on the verification machine. The cost is that a <c>codex</c>
    /// helper invocation is indistinguishable from a session here.
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
            bool homePresent = _home is not null && Directory.Exists(_home);
            bool cliPresent = _appServer.IsAvailable;

            if (!homePresent && !cliPresent)
            {
                return (NotDetected(), []);
            }

            CodexLocalScan local = ReadLocal(now);
            IReadOnlyList<DetectedProcess> processes = await ScanProcessesAsync(ct).ConfigureAwait(false);
            bool processRunning = ProcessScanner.HasProcess(processes, CodexProviderInfo.Id);

            CodexLiveResult live = await TryLiveAsync(cliPresent, now, ct).ConfigureAwait(false);

            CodexRateLimitSnapshot? snapshot = live.RateLimits ?? local.Snapshot;
            IReadOnlyList<UsageMetric> metrics = snapshot is null
                ? []
                : CodexMetricFactory.Build(snapshot.Windows, MetricConfidence.BestEffort);

            TokenTotals? tokens = ToTotals(live.AccountUsage?.LifetimeTokens ?? local.Tokens);
            string? detail = DescribeStatus(live, snapshot, now);
            ProviderStatus status = ResolveStatus(processRunning, local, metrics.Count > 0, now);

            IReadOnlyList<AgentSession> sessions = MarkActive(local.Sessions, processRunning, now);
            return (new ProviderUsage(CodexProviderInfo.Id, status, metrics, tokens, now, detail), sessions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A provider-level failure is a status, never a crash, and it carries no
            // metrics: an unavailable reading is not a zero.
            return (
                new ProviderUsage(CodexProviderInfo.Id, ProviderStatus.Error, [], null, now, "Unable to retrieve usage"),
                []);
        }
    }

    private static ProviderUsage NotDetected() =>
        new(CodexProviderInfo.Id, ProviderStatus.NotDetected, [], null, null, "Codex is not installed on this machine");

    private static TokenTotals? ToTotals(CodexTokenCounts? counts)
    {
        if (counts is not { HasAny: true } value)
        {
            return null;
        }

        // Codex reports no cache-write figure. Null says so; zero would claim the cache was
        // never written to.
        return new TokenTotals(value.Input, value.Output, value.CachedInput, null);
    }

    private static IReadOnlyList<AgentSession> MarkActive(IReadOnlyList<AgentSession> sessions, bool processRunning, DateTimeOffset now)
    {
        if (!processRunning)
        {
            return sessions;
        }

        // A running process cannot be tied to a particular thread without reading its
        // command line, so the newest recently-touched thread is the one marked active.
        AgentSession? newest = null;
        foreach (AgentSession session in sessions)
        {
            if (session.LastActivityAt is null)
            {
                continue;
            }

            if (newest?.LastActivityAt is null || session.LastActivityAt > newest.LastActivityAt)
            {
                newest = session;
            }
        }

        if (newest is null)
        {
            return sessions;
        }

        var marked = new List<AgentSession>(sessions.Count);
        foreach (AgentSession session in sessions)
        {
            marked.Add(ReferenceEquals(session, newest) ? session with { IsActive = true } : session);
        }

        _ = now;
        return marked;
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
            return [];
        }
    }

    private async Task<CodexLiveResult> TryLiveAsync(bool cliPresent, DateTimeOffset now, CancellationToken ct)
    {
        if (!cliPresent)
        {
            return CodexLiveResult.NotDetected;
        }

        if (!_options.AllowNetworkCalls)
        {
            return CodexLiveResult.Skipped;
        }

        if (_lastLiveAttemptAt is { } last && now - last < _options.MinimumLiveCallInterval)
        {
            return CodexLiveResult.Skipped;
        }

        await EnsureAuthModeAsync(now, ct).ConfigureAwait(false);
        if (_authMode is CodexAuthMode.ApiKey or CodexAuthMode.NotAuthenticated)
        {
            return CodexLiveResult.Skipped;
        }

        _lastLiveAttemptAt = now;
        return await _appServer.ReadAsync(_options.LiveCallTimeout, ct).ConfigureAwait(false);
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

        foreach (AgentSession session in local.Sessions)
        {
            if (session.LastActivityAt is { } activity && now - activity <= _options.SessionActivityWindow)
            {
                return ProviderStatus.Active;
            }
        }

        return hasMetrics || local.Sessions.Count > 0 ? ProviderStatus.Idle : ProviderStatus.Detected;
    }

    private string? DescribeStatus(CodexLiveResult live, CodexRateLimitSnapshot? snapshot, DateTimeOffset now)
    {
        if (live.Outcome is CodexLiveOutcome.Succeeded && snapshot?.Source is CodexSnapshotSource.Live)
        {
            return null;
        }

        if (snapshot is null)
        {
            return live.Outcome switch
            {
                CodexLiveOutcome.NotDetected => "Codex CLI not found; no local quota snapshot either",
                CodexLiveOutcome.Skipped when !_options.AllowNetworkCalls => "Network calls are off and no local quota snapshot was found",
                CodexLiveOutcome.Skipped => "No local quota snapshot found",
                _ => "Live quota unavailable and no local snapshot was found",
            };
        }

        string age = UsageReadings.DescribeAge(snapshot.ObservedAt, now);
        return live.Outcome switch
        {
            CodexLiveOutcome.NotDetected => "Codex CLI not found; showing a local snapshot from " + age,
            CodexLiveOutcome.Skipped when !_options.AllowNetworkCalls => "Network calls are off; showing a local snapshot from " + age,
            CodexLiveOutcome.Skipped when _authMode is CodexAuthMode.ApiKey =>
                "Live quota is unavailable under API-key authentication; showing a local snapshot from " + age,
            CodexLiveOutcome.Skipped when _authMode is CodexAuthMode.NotAuthenticated =>
                "Codex is not signed in; showing a local snapshot from " + age,
            CodexLiveOutcome.Skipped => "Showing a local snapshot from " + age,
            CodexLiveOutcome.TimedOut => "The live quota call timed out; showing a local snapshot from " + age,
            _ => "The live quota call failed; showing a local snapshot from " + age,
        };
    }

    private CodexLocalScan ReadLocal(DateTimeOffset now)
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

        List<string> candidates = rows
            .Select(static row => row.RolloutPath)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .ToList();

        if (candidates.Count == 0)
        {
            // No state database, or a schema this reader does not understand. Fall back to
            // the newest files by modification time, still bounded and still shallow.
            candidates = [.. CodexPaths.FindRecentRollouts(_home, _options.MaxSessionsToRead)];
        }

        CodexRateLimitSnapshot? best = null;
        long input = 0;
        long cached = 0;
        long output = 0;
        long reasoning = 0;
        long total = 0;
        bool sawTokens = false;

        var tokensByIndex = new Dictionary<int, CodexTokenCounts>();

        for (int i = 0; i < candidates.Count; i++)
        {
            string path = candidates[i];
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
                tokensByIndex[i] = counts;
                input += counts.Input ?? 0;
                cached += counts.CachedInput ?? 0;
                output += counts.Output ?? 0;
                reasoning += counts.ReasoningOutput ?? 0;
                total += counts.Total ?? 0;
            }
        }

        var sessions = new List<AgentSession>();
        for (int i = 0; i < rows.Count; i++)
        {
            AgentSession? session = ToSession(rows[i].Summary, tokensByIndex.TryGetValue(i, out CodexTokenCounts counts) ? counts : null);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        _ = now;

        CodexTokenCounts? summed = sawTokens
            ? new CodexTokenCounts(input, cached, output, reasoning, total)
            : null;

        return new CodexLocalScan(best, summed, sessions);
    }

    private static AgentSession? ToSession(CodexThreadSummary summary, CodexTokenCounts? counts)
    {
        if (summary.ThreadId is not { } id)
        {
            return null;
        }

        DateTimeOffset? started = summary.CreatedAt ?? summary.UpdatedAt;
        if (started is not { } startedAt)
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

    private sealed record CodexLocalScan(
        CodexRateLimitSnapshot? Snapshot,
        CodexTokenCounts? Tokens,
        IReadOnlyList<AgentSession> Sessions)
    {
        public static CodexLocalScan Empty { get; } = new(null, null, []);
    }
}
