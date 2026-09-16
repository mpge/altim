using System.Globalization;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Providers.Codex;
using Altim.Providers.Codex.AppServer;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// End-to-end provider behaviour: live first, local snapshot with its age stated when the
/// live call cannot be had, and one consistent answer across the ticks in between.
/// </summary>
public sealed class CodexUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 16, 0, 0, TimeSpan.Zero);

    private static string RolloutLine(string timestamp, double percent, int windowMinutes) =>
        "{\"type\":\"event_msg\",\"timestamp\":\"" + timestamp + "\""
        + ",\"payload\":{\"type\":\"token_count\""
        + ",\"rate_limits\":{\"primary\":{\"used_percent\":" + percent.ToString("0.###", CultureInfo.InvariantCulture)
        + ",\"window_minutes\":" + windowMinutes.ToString(CultureInfo.InvariantCulture)
        + ",\"resets_at\":1789549200},\"secondary\":null}"
        + ",\"info\":{\"total_token_usage\":{\"input_tokens\":1200,\"cached_input_tokens\":400"
        + ",\"cache_write_input_tokens\":250,\"output_tokens\":300,\"total_tokens\":1900}}}}";

    private static TempWorkspace CreateHomeWithRollout(double percent = 41.5, string timestamp = "2026-09-15T12:00:00Z")
    {
        var workspace = new TempWorkspace();
        _ = workspace.WriteLines("sessions/2026/09/15/rollout-2026-09-15T12-00-00-0199aaaa.jsonl", RolloutLine(timestamp, percent, 10080));
        return workspace;
    }

    private static CodexRateLimitSnapshot LiveSnapshot(double percent, DateTimeOffset observedAt) =>
        new(
            [new CodexLimitWindow("codex", 10080, percent, DateTimeOffset.FromUnixTimeSeconds(1789549200), null)],
            "pro",
            new CodexCredits(true, false, 10d),
            null,
            CodexSnapshotSource.Live,
            observedAt);

    private static CodexUsageProvider CreateProvider(
        string? home,
        CodexLiveResult liveResult,
        bool cliAvailable = true,
        CodexOptions? options = null,
        FakeCliRunner? runner = null)
    {
        runner ??= new FakeCliRunner { CommandExists = cliAvailable };
        return new CodexUsageProvider(
            options ?? CodexOptions.Default,
            new StubAppServerClient(liveResult, cliAvailable),
            runner,
            new FakeProcessMonitor(),
            home,
            new FixedTimeProvider(Now));
    }

    [Fact]
    public async Task ReportsNotDetectedWhenNeitherTheHomeNorTheCliExists()
    {
        using var workspace = new TempWorkspace();
        using CodexUsageProvider provider = CreateProvider(
            Path.Combine(workspace.Root, "no-such-home"),
            CodexLiveResult.NotDetected,
            cliAvailable: false);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderStatus.NotDetected, usage.Status);
        Assert.Empty(usage.Metrics);
        Assert.Null(usage.Tokens);
        Assert.NotNull(usage.StatusDetail);
    }

    [Fact]
    public async Task AFailedLiveCallFallsBackToTheLocalSnapshotAndSaysHowOldItIs()
    {
        using TempWorkspace workspace = CreateHomeWithRollout(percent: 41.5, timestamp: "2026-09-15T12:00:00Z");
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("doctor --json", """{"schemaVersion":1,"auth":{"mode":"chatgpt"}}""");

        using CodexUsageProvider provider = CreateProvider(workspace.Root, CodexLiveResult.Failed, runner: runner);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        UsageMetric metric = Assert.Single(usage.Metrics);
        Assert.Equal("codex:10080", metric.Key);
        Assert.Equal(41.5d, metric.UsedPercent);
        Assert.Equal(MetricConfidence.BestEffort, metric.Confidence);

        Assert.NotNull(usage.StatusDetail);
        Assert.Contains("local snapshot", usage.StatusDetail, StringComparison.Ordinal);

        // The rollout line is four hours older than the clock, and the note says so rather
        // than presenting the number as current.
        Assert.Contains("4 hours ago", usage.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimedOutLiveCallIsDistinguishedFromAFailedOne()
    {
        using TempWorkspace workspace = CreateHomeWithRollout();
        using CodexUsageProvider provider = CreateProvider(workspace.Root, CodexLiveResult.TimedOut);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(usage.StatusDetail);
        Assert.Contains("timed out", usage.StatusDetail, StringComparison.Ordinal);
        Assert.Single(usage.Metrics);
    }

    [Fact]
    public async Task TheLiveCallIsNotMadeAtAllWhenNetworkCallsAreOff()
    {
        using TempWorkspace workspace = CreateHomeWithRollout();
        var stub = new StubAppServerClient(CodexLiveResult.Failed);

        using var provider = new CodexUsageProvider(
            CodexOptions.Default with { AllowNetworkCalls = false },
            stub,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now));

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stub.CallCount);
        Assert.Single(usage.Metrics);
        Assert.NotNull(usage.StatusDetail);
        Assert.Contains("Network calls are off", usage.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLiveCallIsSkippedUnderApiKeyAuthentication()
    {
        using TempWorkspace workspace = CreateHomeWithRollout();
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("doctor --json", """{"schemaVersion":1,"auth_mode":"api_key"}""");

        var stub = new StubAppServerClient(CodexLiveResult.Failed);
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            stub,
            runner,
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now));

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stub.CallCount);
        Assert.NotNull(usage.StatusDetail);
        Assert.Contains("API-key", usage.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulLiveCallWinsAndCarriesNoStalenessNote()
    {
        using TempWorkspace workspace = CreateHomeWithRollout(percent: 41.5);

        var snapshot = new CodexRateLimitSnapshot(
            [new CodexLimitWindow("codex", 300, 88d, DateTimeOffset.FromUnixTimeSeconds(1789515600), null)],
            "pro",
            new CodexCredits(true, false, 10d),
            null,
            CodexSnapshotSource.Live,
            Now);

        using CodexUsageProvider provider = CreateProvider(
            workspace.Root,
            new CodexLiveResult(CodexLiveOutcome.Succeeded, snapshot, null));

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        UsageMetric metric = Assert.Single(usage.Metrics);
        Assert.Equal("codex:300", metric.Key);
        Assert.Equal(88d, metric.UsedPercent);
        Assert.Null(usage.StatusDetail);
        Assert.Equal(MetricConfidence.BestEffort, metric.Confidence);
    }

    [Fact]
    public async Task ASkippedLiveCallKeepsAnsweringWithTheLastLiveReadingRatherThanAStaleLocalOne()
    {
        // The alternation this exists to stop: with the popup open the scheduler ticks
        // every few seconds, the minute-long floor skips five calls out of six, and the
        // user saw one live reading followed by five stale ones, with the status line
        // flipping and the history table recording both.
        using TempWorkspace workspace = CreateHomeWithRollout(percent: 41.5, timestamp: "2026-09-15T12:00:00Z");

        var stub = new StubAppServerClient(new CodexLiveResult(CodexLiveOutcome.Succeeded, LiveSnapshot(88d, Now), null));
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("doctor --json", """{"schemaVersion":1,"auth_mode":"chatgpt"}""");

        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            stub,
            runner,
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now));

        ProviderUsage first = await provider.GetUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(88d, Assert.Single(first.Metrics).UsedPercent);
        Assert.Null(first.StatusDetail);

        // Five more ticks inside the floor. Every one of them is the same reading.
        for (int tick = 0; tick < 5; tick++)
        {
            await provider.RefreshAsync(TestContext.Current.CancellationToken);
            ProviderUsage repeat = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

            Assert.Equal(88d, Assert.Single(repeat.Metrics).UsedPercent);
            Assert.Null(repeat.StatusDetail);
        }

        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task ASkippedCallNeverDemotesALiveReadingToAnOlderLocalOne()
    {
        // The local snapshot is four hours old. Even once the live reading has aged past
        // its retention window, it is still the fresher of the two and stays the answer.
        using TempWorkspace workspace = CreateHomeWithRollout(percent: 41.5, timestamp: "2026-09-15T12:00:00Z");

        var stub = new StubAppServerClient(new CodexLiveResult(CodexLiveOutcome.Succeeded, LiveSnapshot(88d, Now), null));
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("doctor --json", """{"schemaVersion":1,"auth_mode":"chatgpt"}""");

        var clock = new MovableTimeProvider(Now);
        using var provider = new CodexUsageProvider(
            CodexOptions.Default with { LiveSnapshotRetention = TimeSpan.FromMinutes(5) },
            stub,
            runner,
            new FakeProcessMonitor(),
            workspace.Root,
            clock);

        _ = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        // Ten minutes on, past the retention window, with the live call now failing.
        stub.Result = CodexLiveResult.Failed;
        clock.Advance(TimeSpan.FromMinutes(10));
        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(88d, Assert.Single(usage.Metrics).UsedPercent);
        Assert.NotNull(usage.StatusDetail);
        Assert.Contains("last live reading", usage.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALiveReadingWithNoWindowsIsASuccessThatRemovesTheMeters()
    {
        // A family that stops reporting a window loses its meter. It does not keep the last
        // number it had, and it does not send the reader back to a local file.
        using TempWorkspace workspace = CreateHomeWithRollout(percent: 41.5);

        var empty = new CodexRateLimitSnapshot([], "pro", null, null, CodexSnapshotSource.Live, Now);
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("doctor --json", """{"schemaVersion":1,"auth_mode":"chatgpt"}""");

        using CodexUsageProvider provider = CreateProvider(
            workspace.Root,
            new CodexLiveResult(CodexLiveOutcome.Succeeded, empty, null),
            runner: runner);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Empty(usage.Metrics);
        Assert.Null(usage.StatusDetail);
    }

    [Fact]
    public async Task TheLiveCallGoesThroughTheRefreshGateAndTheLocalReadDoesNot()
    {
        using TempWorkspace workspace = CreateHomeWithRollout(percent: 41.5);

        var stub = new StubAppServerClient(new CodexLiveResult(CodexLiveOutcome.Succeeded, LiveSnapshot(88d, Now), null));
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("doctor --json", """{"schemaVersion":1,"auth_mode":"chatgpt"}""");
        var gate = new SwitchableRefreshGate { IsOpen = false };

        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            stub,
            runner,
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            gate);

        ProviderUsage closed = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        // No live call, and the local sources answered anyway: a closed network gate holds
        // back one call, not the whole refresh.
        Assert.Equal(0, stub.CallCount);
        Assert.Equal(CodexProviderInfo.Id, Assert.Single(gate.Requested));
        Assert.Equal(41.5d, Assert.Single(closed.Metrics).UsedPercent);
        Assert.NotNull(closed.Tokens);

        gate.IsOpen = true;
        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        ProviderUsage opened = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stub.CallCount);
        Assert.Equal(88d, Assert.Single(opened.Metrics).UsedPercent);
    }

    [Fact]
    public async Task LocalTokensAreReportedFromTheCumulativeFieldOfEachSession()
    {
        using TempWorkspace workspace = CreateHomeWithRollout();
        using CodexUsageProvider provider = CreateProvider(workspace.Root, CodexLiveResult.Failed);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(usage.Tokens);
        Assert.Equal(1200L, usage.Tokens.Input);
        Assert.Equal(300L, usage.Tokens.Output);
        Assert.Equal(400L, usage.Tokens.CacheRead);

        // cache_write_input_tokens is in the real schema and is carried through.
        Assert.Equal(250L, usage.Tokens.CacheWrite);
    }

    [Fact]
    public async Task AnUnreportedTokenComponentStaysUnavailableRatherThanBecomingZero()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "sessions/2026/09/15/rollout-partial.jsonl",
            """{"type":"event_msg","timestamp":"2026-09-15T12:00:00Z","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":10,"window_minutes":10080,"resets_at":1789549200}},"info":{"total_token_usage":{"input_tokens":50,"output_tokens":20}}}}""");

        using CodexUsageProvider provider = CreateProvider(workspace.Root, CodexLiveResult.Failed);
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(usage.Tokens);
        Assert.Equal(50L, usage.Tokens.Input);
        Assert.Equal(20L, usage.Tokens.Output);
        Assert.Null(usage.Tokens.CacheRead);
        Assert.Null(usage.Tokens.CacheWrite);
    }

    [Fact]
    public async Task ServerLifetimeTotalsAreReportedApartFromLocallyObservedOnes()
    {
        // One field cannot hold both. The server reports a lifetime grand total with no
        // breakdown; the local sum is per component and measured about 16 per cent away
        // from it. Putting them in the same field made the meaning of the number depend on
        // whether the last call happened to succeed.
        using TempWorkspace workspace = CreateHomeWithRollout();

        var live = new CodexLiveResult(
            CodexLiveOutcome.Succeeded,
            LiveSnapshot(88d, Now),
            new CodexAccountUsage(987_654_321L, 97L, 4L, 11L, 5_000_000L));

        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("doctor --json", """{"schemaVersion":1,"auth_mode":"chatgpt"}""");

        using CodexUsageProvider provider = CreateProvider(workspace.Root, live, runner: runner);
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1200L, usage.Tokens?.Input);
        Assert.Equal(987_654_321L, provider.ServerReportedUsage?.LifetimeTokens);
        Assert.Equal(MetricConfidence.BestEffort, provider.ServerReportedUsage?.Confidence);
        Assert.Equal(Now, provider.ServerReportedUsageAt);
    }

    [Fact]
    public async Task AHomeWithNoQuotaSnapshotReportsNoMetricsRatherThanZeroes()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines("sessions/2026/09/15/rollout-empty.jsonl", """{"type":"session_meta","payload":{"id":"abc","timestamp":"2026-09-15T10:00:00Z"}}""");

        using CodexUsageProvider provider = CreateProvider(workspace.Root, CodexLiveResult.Failed);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Empty(usage.Metrics);
        Assert.NotEqual(ProviderStatus.NotDetected, usage.Status);
        Assert.NotNull(usage.StatusDetail);
    }

    [Fact]
    public async Task ADetectedProcessIsProcessPresenceAndDoesNotPromoteAThreadToActive()
    {
        // A "codex" process may be a helper invocation, and telling which thread it belongs
        // to would mean reading its command line. The installation is reported as active;
        // no particular session is.
        using TempWorkspace workspace = CreateHomeWithRollout();

        using var provider = new CodexUsageProvider(
            CodexOptions.Default with { AllowNetworkCalls = false },
            new StubAppServerClient(CodexLiveResult.Skipped),
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(new DetectedProcess(4242, CodexProviderInfo.Id, "codex", Now)),
            workspace.Root,
            new FixedTimeProvider(Now));

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<AgentSession> sessions = await provider.GetSessionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderStatus.Active, usage.Status);
        Assert.DoesNotContain(sessions, static session => session.IsActive);
    }

    [Fact]
    public async Task AFailedReadingCarriesTheFixedSentenceAndNeverTheExceptionText()
    {
        // Exception messages name files, and on the verification machine they named other
        // people's project directories. The exception goes to Core, which routes it to its
        // own event for logging; what reaches the reading — and therefore the screen — is
        // one fixed sentence that says nothing about this machine.
        using TempWorkspace workspace = CreateHomeWithRollout();

        using var provider = new CodexUsageProvider(
            CodexOptions.Default with { AllowNetworkCalls = false },
            new StubAppServerClient(CodexLiveResult.Skipped),
            new FakeCliRunner { CommandExists = true },
            new ThrowingProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now));

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderStatus.Error, usage.Status);
        Assert.Equal(ProviderUsage.UnavailableDetail, usage.StatusDetail);
        Assert.DoesNotContain("secret-project", usage.StatusDetail, StringComparison.OrdinalIgnoreCase);

        // An unavailable reading is not a zero, and it is not a half-filled one either:
        // the rollout tail did produce numbers on the way past, and none of them is kept.
        Assert.Empty(usage.Metrics);
        Assert.Null(usage.Tokens);
        Assert.Empty(await provider.GetSessionsAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The setting is live: a provider outlives every settings change, so switching the
    /// permission off has to stop the next call rather than the next restart.
    /// </summary>
    [Fact]
    public async Task SwitchingTheNetworkPolicyOffStopsTheNextLiveCall()
    {
        using TempWorkspace workspace = CreateHomeWithRollout();
        var stub = new StubAppServerClient(new CodexLiveResult(CodexLiveOutcome.Succeeded, LiveSnapshot(77d, Now), null));
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("doctor --json", """{"schemaVersion":1,"auth_mode":"chatgpt"}""");

        var policy = new MutableNetworkPolicy { AllowsNetworkCalls = true };

        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            stub,
            runner,
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            networkPolicy: policy);

        ProviderUsage live = await provider.GetUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, stub.CallCount);
        Assert.Equal(77d, Assert.Single(live.Metrics).UsedPercent);

        policy.AllowsNetworkCalls = false;
        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        ProviderUsage local = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, stub.CallCount);

        // The server's answer is forgotten rather than kept as the current reading: the
        // local rollout tail is now the only source, and the status line says so.
        Assert.Equal(41.5d, Assert.Single(local.Metrics).UsedPercent);
        Assert.NotNull(local.StatusDetail);
        Assert.Contains("Network calls are off", local.StatusDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A provider constructed with network calls off stays off whatever the policy says.
    /// </summary>
    [Fact]
    public async Task TheConstructedOptionAndTheLivePolicyBothHaveToAllowTheCall()
    {
        using TempWorkspace workspace = CreateHomeWithRollout();
        var stub = new StubAppServerClient(CodexLiveResult.Failed);

        using var provider = new CodexUsageProvider(
            CodexOptions.Default with { AllowNetworkCalls = false },
            stub,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            networkPolicy: new MutableNetworkPolicy { AllowsNetworkCalls = true });

        _ = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task RaisesUsageChangedOnceWhenTheReadingMoves()
    {
        using TempWorkspace workspace = CreateHomeWithRollout();
        using CodexUsageProvider provider = CreateProvider(
            workspace.Root,
            CodexLiveResult.Failed,
            options: CodexOptions.Default with { AllowNetworkCalls = false });

        int raised = 0;
        provider.UsageChanged += (_, _) => raised++;

        await provider.RefreshAsync(TestContext.Current.CancellationToken);
        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, raised);
    }
}
