using System.Globalization;
using Altim.Core.Models;
using Altim.Providers.Codex;
using Altim.Providers.Codex.AppServer;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// End-to-end provider behaviour: live first, local snapshot with its age stated when the
/// live call cannot be had.
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
        + ",\"output_tokens\":300,\"total_tokens\":1900}}}}";

    private static TempWorkspace CreateHomeWithRollout(double percent = 41.5, string timestamp = "2026-09-15T12:00:00Z")
    {
        var workspace = new TempWorkspace();
        _ = workspace.WriteLines("sessions/2026/09/15/rollout-2026-09-15T12-00-00-0199aaaa.jsonl", RolloutLine(timestamp, percent, 10080));
        return workspace;
    }

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
            10d,
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
    public async Task LocalTokensAreReportedFromTheCumulativeFieldOfEachSession()
    {
        using TempWorkspace workspace = CreateHomeWithRollout();
        using CodexUsageProvider provider = CreateProvider(workspace.Root, CodexLiveResult.Failed);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(usage.Tokens);
        Assert.Equal(1200L, usage.Tokens.Input);
        Assert.Equal(300L, usage.Tokens.Output);
        Assert.Equal(400L, usage.Tokens.CacheRead);

        // Codex reports no cache-write figure at all, and null says exactly that.
        Assert.Null(usage.Tokens.CacheWrite);
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
