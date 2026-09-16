using System.Globalization;
using Altim.Core.Models;
using Altim.Providers.Claude;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// How the three Claude Code sources combine, and what happens when each is missing.
/// </summary>
public sealed class ClaudeUsageProviderTests
{
    private const string UsageArguments = "-p --output-format json /usage";
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 16, 0, 0, TimeSpan.Zero);

    private static string StatusLinePayload(DateTimeOffset writtenAt, int fiveHour = 53, int sevenDay = 85) =>
        "{\"written_at\":" + N(writtenAt.ToUnixTimeSeconds())
        + ",\"session_id\":\"0199aaaa-bbbb-cccc-dddd-eeeeffff0000\""
        + ",\"cwd\":\"C:\\\\work\\\\private-project\""
        + ",\"rate_limits\":{"
        + "\"five_hour\":{\"used_percentage\":" + N(fiveHour) + ",\"resets_at\":1789515600},"
        + "\"seven_day\":{\"used_percentage\":" + N(sevenDay) + ",\"resets_at\":1789549200}}"
        + ",\"cost\":{\"total_cost_usd\":1.25}}";

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static ClaudeUsageProvider CreateProvider(TempWorkspace workspace, FakeCliRunner runner) =>
        new(
            ClaudeOptions.Default,
            runner,
            new FakeProcessMonitor(),
            [workspace.Root],
            new FixedTimeProvider(Now));

    [Fact]
    public async Task ReportsNotDetectedWhenNeitherTheCliNorAConfigRootExists()
    {
        using var provider = new ClaudeUsageProvider(
            ClaudeOptions.Default,
            new FakeCliRunner { CommandExists = false },
            new FakeProcessMonitor(),
            [],
            new FixedTimeProvider(Now));

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderStatus.NotDetected, usage.Status);
        Assert.Empty(usage.Metrics);
        Assert.Null(usage.Tokens);
    }

    [Fact]
    public async Task TheStatusLineSuppliesDocumentedWindowsWithTheirResetInstants()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.Write(ClaudePaths.StatusLineStateFileName, StatusLinePayload(Now.AddMinutes(-1)));

        using ClaudeUsageProvider provider = CreateProvider(workspace, new FakeCliRunner { CommandExists = false });
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        UsageMetric fiveHour = Assert.Single(usage.Metrics, m => m.Key == "five_hour");
        Assert.Equal("Session", fiveHour.Label);
        Assert.Equal(53d, fiveHour.UsedPercent);
        Assert.Equal(MetricConfidence.Documented, fiveHour.Confidence);
        Assert.Equal(TimeSpan.FromHours(5), fiveHour.Window?.Length);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789515600), fiveHour.Window?.ResetsAt);

        UsageMetric weekly = Assert.Single(usage.Metrics, m => m.Key == "seven_day");
        Assert.Equal(85d, weekly.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789549200), weekly.Window?.ResetsAt);

        Assert.Null(usage.StatusDetail);
    }

    [Fact]
    public async Task AStaleStatusLineReadingIsLabelledWithItsAge()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.Write(ClaudePaths.StatusLineStateFileName, StatusLinePayload(Now.AddMinutes(-30)));

        using ClaudeUsageProvider provider = CreateProvider(workspace, new FakeCliRunner { CommandExists = false });
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(usage.StatusDetail);
        Assert.Contains("30 minutes ago", usage.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(53d, Assert.Single(usage.Metrics, m => m.Key == "five_hour").UsedPercent);
    }

    [Fact]
    public async Task AWindowMissingFromTheStatusLinePayloadProducesNoMetric()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.Write(
            ClaudePaths.StatusLineStateFileName,
            "{\"written_at\":" + N(Now.ToUnixTimeSeconds())
            + ",\"rate_limits\":{\"seven_day\":{\"used_percentage\":85,\"resets_at\":1789549200}}}");

        using ClaudeUsageProvider provider = CreateProvider(workspace, new FakeCliRunner { CommandExists = false });
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(usage.Metrics, m => m.Key == "five_hour");
        Assert.Single(usage.Metrics);
    }

    [Fact]
    public async Task TheHeadlessSummaryAddsTheOpusAndSonnetWeekliesTheStatusLineDoesNotExpose()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.Write(ClaudePaths.StatusLineStateFileName, StatusLinePayload(Now));

        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson(
            UsageArguments,
            """{"num_turns":0,"total_cost_usd":0,"result":"Current session: 53% used\nCurrent week (all models): 85% used\nCurrent week (Opus): 12% used\nCurrent week (Sonnet): 40% used"}""");

        using ClaudeUsageProvider provider = CreateProvider(workspace, runner);
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        UsageMetric opus = Assert.Single(usage.Metrics, m => m.Key == "seven_day_opus");
        Assert.Equal(12d, opus.UsedPercent);
        Assert.Equal(MetricConfidence.BestEffort, opus.Confidence);

        Assert.Equal(40d, Assert.Single(usage.Metrics, m => m.Key == "seven_day_sonnet").UsedPercent);

        // The documented source still owns the windows it reports; the summary does not
        // displace it or get averaged into it.
        UsageMetric fiveHour = Assert.Single(usage.Metrics, m => m.Key == "five_hour");
        Assert.Equal(MetricConfidence.Documented, fiveHour.Confidence);
    }

    [Fact]
    public async Task TheHeadlessSummaryIsNotRunWhenNetworkCallsAreOff()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeCliRunner { CommandExists = true };

        using var provider = new ClaudeUsageProvider(
            ClaudeOptions.Default with { AllowNetworkCalls = false },
            runner,
            new FakeProcessMonitor(),
            [workspace.Root],
            new FixedTimeProvider(Now));

        _ = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(UsageArguments, runner.Invocations);
    }

    [Fact]
    public async Task TranscriptTokensAreReportedWithCacheTiersSummedOnlyAtTheContractBoundary()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            """{"type":"assistant","sessionId":"0199aaaa-bbbb-cccc-dddd-eeeeffff0000","requestId":"req_1","message":{"id":"msg_1","model":"claude-opus-4-5-20260101","usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":300,"cache_creation":{"ephemeral_5m_input_tokens":40,"ephemeral_1h_input_tokens":50}}}}""");

        using ClaudeUsageProvider provider = CreateProvider(workspace, new FakeCliRunner { CommandExists = false });
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(usage.Tokens);
        Assert.Equal(10L, usage.Tokens.Input);
        Assert.Equal(20L, usage.Tokens.Output);
        Assert.Equal(300L, usage.Tokens.CacheRead);
        Assert.Equal(90L, usage.Tokens.CacheWrite);
    }

    [Fact]
    public async Task RunningSessionsComeFromTheCliAgentsListing()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            """{"type":"assistant","sessionId":"0199aaaa-bbbb-cccc-dddd-eeeeffff0000","requestId":"req_1","message":{"id":"msg_1","model":"claude-opus-4-5-20260101","usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0}}}}""");

        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson(
            "agents --json",
            """[{"pid":4242,"cwd":"C:\\work\\private-project","kind":"main","startedAt":"2026-09-15T15:30:00Z","sessionId":"0199aaaa-bbbb-cccc-dddd-eeeeffff0000","name":"private-project","status":"running"}]""");

        using ClaudeUsageProvider provider = CreateProvider(workspace, runner);
        IReadOnlyList<AgentSession> sessions = await provider.GetSessionsAsync(TestContext.Current.CancellationToken);

        AgentSession session = Assert.Single(sessions);
        Assert.Equal("0199aaaa-bbbb-cccc-dddd-eeeeffff0000", session.Id);
        Assert.Equal(ClaudeProviderInfo.Id, session.ProviderId);
        Assert.True(session.IsActive);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 15, 30, 0, TimeSpan.Zero), session.StartedAt);
        Assert.Equal(20L, session.Tokens?.Output);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProviderStatus.Active, usage.Status);
    }

    [Fact]
    public async Task NoStatusLineAndNoSummaryMeansNoMetricsRatherThanZeroes()
    {
        using var workspace = new TempWorkspace();
        using ClaudeUsageProvider provider = CreateProvider(workspace, new FakeCliRunner { CommandExists = false });

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Empty(usage.Metrics);
        Assert.NotNull(usage.StatusDetail);
        Assert.NotEqual(ProviderStatus.NotDetected, usage.Status);
    }
}
