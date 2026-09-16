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

    private static string AssistantLine(string messageId, long input, long output) =>
        "{\"type\":\"assistant\",\"sessionId\":\"0199aaaa-bbbb-cccc-dddd-eeeeffff0000\""
        + ",\"requestId\":\"req_" + messageId + "\""
        + ",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"claude-opus-4-5-20260101\""
        + ",\"usage\":{\"input_tokens\":" + N(input) + ",\"output_tokens\":" + N(output) + "}}}";

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
    public async Task AStaleStatusLineDoesNotOutrankAFresherSummaryReading()
    {
        // Last session Friday, Altim opened on Monday. The status-line file is documented
        // and three days old; the summary was read a moment ago.
        using var workspace = new TempWorkspace();
        _ = workspace.Write(ClaudePaths.StatusLineStateFileName, StatusLinePayload(Now.AddDays(-3), fiveHour: 85, sevenDay: 90));

        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson(
            UsageArguments,
            """{"num_turns":0,"total_cost_usd":0,"result":"Current session: 4% used\nCurrent week (all models): 31% used"}""");

        using ClaudeUsageProvider provider = CreateProvider(workspace, runner);
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        UsageMetric fiveHour = Assert.Single(usage.Metrics, m => m.Key == "five_hour");
        Assert.Equal(4d, fiveHour.UsedPercent);
        Assert.Equal(MetricConfidence.BestEffort, fiveHour.Confidence);
        Assert.Equal(31d, Assert.Single(usage.Metrics, m => m.Key == "seven_day").UsedPercent);
    }

    [Fact]
    public async Task AWindowWhoseResetHasAlreadyPassedProducesNoMeter()
    {
        // Friday's file still says 85 per cent of a five-hour window, and that window
        // reset days ago.
        using var workspace = new TempWorkspace();
        _ = workspace.Write(
            ClaudePaths.StatusLineStateFileName,
            "{\"written_at\":" + N(Now.AddDays(-3).ToUnixTimeSeconds())
            + ",\"rate_limits\":{\"five_hour\":{\"used_percentage\":85,\"resets_at\":" + N(Now.AddDays(-3).AddHours(1).ToUnixTimeSeconds()) + "}"
            + ",\"seven_day\":{\"used_percentage\":60,\"resets_at\":" + N(Now.AddDays(2).ToUnixTimeSeconds()) + "}}}");

        using ClaudeUsageProvider provider = CreateProvider(workspace, new FakeCliRunner { CommandExists = false });
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(usage.Metrics, m => m.Key == "five_hour");
        Assert.Equal(60d, Assert.Single(usage.Metrics, m => m.Key == "seven_day").UsedPercent);
    }

    [Fact]
    public async Task TheSpendLimitKeepsItsResetInstant()
    {
        // The spend limit reports a percentage and a reset and no period. Building a window
        // only when a length was known threw the reset away, which was the one thing on
        // that row worth showing.
        using var workspace = new TempWorkspace();
        long resetsAt = Now.AddDays(9).ToUnixTimeSeconds();
        _ = workspace.Write(
            ClaudePaths.StatusLineStateFileName,
            "{\"written_at\":" + N(Now.ToUnixTimeSeconds())
            + ",\"rate_limits\":{\"spend_limit\":{\"used_percentage\":12,\"resets_at\":" + N(resetsAt) + "}}}");

        using ClaudeUsageProvider provider = CreateProvider(workspace, new FakeCliRunner { CommandExists = false });
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        UsageMetric spend = Assert.Single(usage.Metrics, m => m.Key == "spend_limit");
        Assert.Equal(12d, spend.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(resetsAt), spend.Window?.ResetsAt);
    }

    [Fact]
    public async Task PerSessionTotalsKeepAddingUpAcrossRefreshes()
    {
        // The scanner returns only the newly appended bytes. Reading the session total out
        // of the pass turned it into a per-refresh delta the moment a second refresh
        // happened, so a session that had spent millions reported a handful.
        using var workspace = new TempWorkspace();
        string transcript = workspace.WriteLines("projects/slug/session.jsonl", AssistantLine("msg_1", input: 10, output: 20));

        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson(
            "agents --json",
            """[{"pid":4242,"startedAt":1789467994766,"sessionId":"0199aaaa-bbbb-cccc-dddd-eeeeffff0000","status":"running"}]""");

        using ClaudeUsageProvider provider = CreateProvider(workspace, runner);
        _ = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        workspace.Append(transcript, AssistantLine("msg_2", input: 5, output: 7) + "\n");
        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        AgentSession session = Assert.Single(await provider.GetSessionsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(15L, session.Tokens?.Input);
        Assert.Equal(27L, session.Tokens?.Output);
    }

    [Fact]
    public async Task ASessionWithNoReportedStartTimeIsNotGivenOne()
    {
        // No row rather than a made-up clock time, and the provider still reports that an
        // agent is running, because the command listing it is what says so.
        using var workspace = new TempWorkspace();
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("agents --json", """[{"pid":4242,"sessionId":"0199aaaa-bbbb-cccc-dddd-eeeeffff0000","status":"running"}]""");

        using ClaudeUsageProvider provider = CreateProvider(workspace, runner);

        Assert.Empty(await provider.GetSessionsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProviderStatus.Active, (await provider.GetUsageAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task AnAuthoritativeEmptyListingIsNotSecondGuessedByProcessEnumeration()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson("agents --json", "[]");

        using var provider = new ClaudeUsageProvider(
            ClaudeOptions.Default,
            runner,
            new FakeProcessMonitor(new DetectedProcess(99, ClaudeProviderInfo.Id, "claude", Now)),
            [workspace.Root],
            new FixedTimeProvider(Now));

        Assert.NotEqual(ProviderStatus.Active, (await provider.GetUsageAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task AFailedListingFallsBackToProcessEnumeration()
    {
        using var workspace = new TempWorkspace();

        // The runner answers nothing for "agents --json", which is a failed run.
        using var provider = new ClaudeUsageProvider(
            ClaudeOptions.Default,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(new DetectedProcess(99, ClaudeProviderInfo.Id, "claude", Now)),
            [workspace.Root],
            new FixedTimeProvider(Now));

        Assert.Equal(ProviderStatus.Active, (await provider.GetUsageAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task AGatewayModelIdIsCountedRatherThanTreatedAsSynthetic()
    {
        // Bedrock and Vertex ids carry colons and slashes. Treating everything that is not
        // a plain identifier as a synthetic placeholder erased every token those
        // deployments ever reported.
        using var workspace = new TempWorkspace();
        _ = workspace.WriteLines(
            "projects/slug/session.jsonl",
            """{"type":"assistant","sessionId":"0199aaaa-bbbb-cccc-dddd-eeeeffff0000","requestId":"req_1","message":{"id":"msg_bedrock","model":"us.anthropic.claude-opus-4-5-20260101-v1:0","usage":{"input_tokens":11,"output_tokens":22}}}""",
            """{"type":"assistant","sessionId":"0199aaaa-bbbb-cccc-dddd-eeeeffff0000","requestId":"req_2","message":{"id":"msg_synthetic","model":"<synthetic>","usage":{"input_tokens":1000,"output_tokens":1000}}}""");

        using ClaudeUsageProvider provider = CreateProvider(workspace, new FakeCliRunner { CommandExists = false });
        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(11L, usage.Tokens?.Input);
        Assert.Equal(22L, usage.Tokens?.Output);
    }

    [Fact]
    public async Task ATransientlyUnreadableStatusLineKeepsTheLastGoodValue()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.Write(ClaudePaths.StatusLineStateFileName, StatusLinePayload(Now));

        using ClaudeUsageProvider provider = CreateProvider(workspace, new FakeCliRunner { CommandExists = false });
        Assert.Equal(53d, Assert.Single((await provider.GetUsageAsync(TestContext.Current.CancellationToken)).Metrics, m => m.Key == "five_hour").UsedPercent);

        // The helper is rewriting the file: it exists and cannot be opened right now.
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await provider.RefreshAsync(TestContext.Current.CancellationToken);
        }

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(53d, Assert.Single(usage.Metrics, m => m.Key == "five_hour").UsedPercent);
    }

    [Fact]
    public async Task TheHeadlessSummaryGoesThroughTheRefreshGate()
    {
        using var workspace = new TempWorkspace();
        var runner = new FakeCliRunner { CommandExists = true };
        runner.RespondWithJson(
            UsageArguments,
            """{"num_turns":0,"total_cost_usd":0,"result":"Current session: 12% used"}""");
        var gate = new SwitchableRefreshGate { IsOpen = false };

        using var provider = new ClaudeUsageProvider(
            ClaudeOptions.Default,
            runner,
            new FakeProcessMonitor(),
            [workspace.Root],
            new FixedTimeProvider(Now),
            "claude",
            gate);

        _ = await provider.GetUsageAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(UsageArguments, runner.Invocations);

        gate.IsOpen = true;
        await provider.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Contains(UsageArguments, runner.Invocations);
        Assert.Equal(12d, Assert.Single((await provider.GetUsageAsync(TestContext.Current.CancellationToken)).Metrics, m => m.Key == "five_hour").UsedPercent);
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

    [Fact]
    public async Task AFailedReadingCarriesTheFixedSentenceAndNeverTheExceptionText()
    {
        // Exception messages name files, and on the verification machine they named other
        // people's project directories. The exception goes to Core, which routes it to its
        // own event for logging; what reaches the reading — and therefore the screen — is
        // one fixed sentence that says nothing about this machine.
        using var workspace = new TempWorkspace();
        _ = workspace.Write(ClaudePaths.StatusLineStateFileName, StatusLinePayload(Now));

        using var provider = new ClaudeUsageProvider(
            ClaudeOptions.Default,
            new FakeCliRunner { CommandExists = false },
            new ThrowingProcessMonitor(),
            [workspace.Root],
            new FixedTimeProvider(Now));

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderStatus.Error, usage.Status);
        Assert.Equal(ProviderUsage.UnavailableDetail, usage.StatusDetail);
        Assert.DoesNotContain("secret-project", usage.StatusDetail, StringComparison.OrdinalIgnoreCase);

        // An unavailable reading is not a zero, and it is not a half-filled one either.
        Assert.Empty(usage.Metrics);
        Assert.Null(usage.Tokens);
    }
}
