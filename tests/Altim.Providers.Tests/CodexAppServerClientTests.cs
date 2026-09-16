using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Altim.Providers.Codex.AppServer;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The JSON-RPC exchange with <c>codex app-server</c>, driven over a fake pair of streams.
/// </summary>
/// <remarks>
/// <para>
/// The protocol is separated from the process plumbing precisely so it can be tested like
/// this: the request lines, the matching of replies to ids, the refusals and the empty
/// answer are all exercised without a CLI, a network, an account or a token.
/// </para>
/// <para>
/// The two tests that do start a child process use a script this test writes, never the real
/// Codex CLI. Asking the real one would have it call OpenAI with the user's own stored
/// credentials and count against their account, which a test is not entitled to do.
/// </para>
/// </remarks>
public sealed class CodexAppServerClientTests
{
    private const string ClientName = "altim";
    private const string ClientVersion = "1.0";
    private const string LockFileName = "child.lock";

    private const string InitializeReply = """{"jsonrpc":"2.0","id":1,"result":{"userAgent":"codex/0.153.4"}}""";

    private const string RateLimitsReply =
        """
        {"jsonrpc":"2.0","id":2,"result":{"rateLimitsByLimitId":{"codex":{"limit_name":"codex","planType":"pro","primary":{"usedPercent":73,"windowDurationMins":300,"resetsAt":1789515600},"secondary":{"usedPercent":41,"windowDurationMins":10080,"resetsAt":1789549200}},"codex_bengalfox":{"primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1789515600}}}}}
        """;

    private const string UsageReply =
        """
        {"jsonrpc":"2.0","id":3,"result":{"summary":{"lifetimeTokens":987654321,"currentStreakDays":4,"longestStreakDays":11,"peakDailyTokens":5000000},"dailyUsageBuckets":[{"day":1},{"day":2},{"day":3}]}}
        """;

    [Fact]
    public async Task TheHandshakeIsInitializeThenTheNotificationThenTheTwoReads()
    {
        var requests = new StringWriter();

        _ = await CodexAppServerClient.ExchangeAsync(
            requests,
            new StringReader(Stream(InitializeReply, RateLimitsReply, UsageReply)),
            ClientName,
            ClientVersion,
            TestContext.Current.CancellationToken);

        string[] lines = requests.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);

        using JsonDocument initialize = JsonDocument.Parse(lines[0]);
        Assert.Equal("initialize", Method(initialize));
        Assert.Equal(CodexAppServerClient.InitializeId, initialize.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("2.0", initialize.RootElement.GetProperty("jsonrpc").GetString());

        JsonElement clientInfo = initialize.RootElement.GetProperty("params").GetProperty("clientInfo");
        Assert.Equal(ClientName, clientInfo.GetProperty("name").GetString());
        Assert.Equal(ClientVersion, clientInfo.GetProperty("version").GetString());

        // The notification carries no id. Giving it one would invite an answer, and a reply
        // with an id nothing is waiting on is indistinguishable from a stray notification.
        using JsonDocument initialized = JsonDocument.Parse(lines[1]);
        Assert.Equal("initialized", Method(initialized));
        Assert.False(initialized.RootElement.TryGetProperty("id", out _));

        using JsonDocument rateLimits = JsonDocument.Parse(lines[2]);
        Assert.Equal("account/rateLimits/read", Method(rateLimits));
        Assert.Equal(CodexAppServerClient.RateLimitsId, rateLimits.RootElement.GetProperty("id").GetInt32());

        using JsonDocument usage = JsonDocument.Parse(lines[3]);
        Assert.Equal("account/usage/read", Method(usage));
        Assert.Equal(CodexAppServerClient.UsageId, usage.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ASuccessfulReplyProducesOneMeterPerFamilyAndWindow()
    {
        CodexLiveResult result = await ExchangeAsync(InitializeReply, RateLimitsReply, UsageReply);

        Assert.Equal(CodexLiveOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.RateLimits);
        Assert.Equal("pro", result.RateLimits.PlanType);

        // rateLimitsByLimitId, not the top-level rateLimits object: that one silently picks
        // a single family, so an account with two would have one of them rendered as if it
        // were the whole picture.
        Assert.Equal(
            "codex:300:73, codex:10080:41, codex_bengalfox:300:12",
            string.Join(", ", result.RateLimits.Windows.Select(Describe)));

        Assert.Equal(987_654_321L, result.AccountUsage?.LifetimeTokens);
        Assert.Equal(3L, result.AccountUsage?.DailyBucketCount);
        Assert.Equal(11L, result.AccountUsage?.LongestStreakDays);
    }

    [Fact]
    public async Task AReplyWithNoWindowsIsASuccessThatMeansNoMetersRatherThanAFailure()
    {
        // An account before its first request of a period reports nothing, and so does a
        // family that has stopped reporting a window. Reading that as a failure sent the
        // caller back to a stale local file and put a number on screen that the live source
        // had just declined to give.
        CodexLiveResult result = await ExchangeAsync(
            InitializeReply,
            """{"jsonrpc":"2.0","id":2,"result":{"rateLimitsByLimitId":{}}}""",
            UsageReply);

        Assert.Equal(CodexLiveOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.RateLimits);
        Assert.Empty(result.RateLimits.Windows);
        Assert.False(result.RateLimits.HasWindows);
    }

    [Fact]
    public async Task AMalformedLineIsSkippedAndTheExchangeStillCompletes()
    {
        // The app-server writes progress chatter that is not always JSON, and a line this
        // reader cannot parse is no reason to abandon a reply that arrives after it.
        CodexLiveResult result = await ExchangeAsync(
            "starting app-server...",
            "{ this is not json",
            "[1, 2, 3]",
            InitializeReply,
            """{"jsonrpc":"2.0","method":"account/rateLimits/updated","params":{"usedPercent":99}}""",
            RateLimitsReply,
            "",
            UsageReply);

        Assert.Equal(CodexLiveOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, result.RateLimits?.Windows.Count);

        // The push notification carried no id, so it was not mistaken for an answer.
        Assert.DoesNotContain(result.RateLimits!.Windows, static window => window.UsedPercent is 99d);
    }

    [Fact]
    public async Task AStreamOfNothingButMalformedLinesLeavesTheCallFailed()
    {
        CodexLiveResult result = await ExchangeAsync("not json", "still not json", "{ nope");

        Assert.Equal(CodexLiveOutcome.Failed, result.Outcome);
        Assert.Null(result.RateLimits);
        Assert.Null(result.AccountUsage);
    }

    [Fact]
    public async Task AStreamThatEndsBeforeTheRepliesArriveIsAFailureRatherThanAHang()
    {
        CodexLiveResult result = await ExchangeAsync(InitializeReply);

        Assert.Equal(CodexLiveOutcome.Failed, result.Outcome);
        Assert.Null(result.RateLimits);
    }

    [Fact]
    public async Task RepliesAreMatchedByIdRatherThanByTheOrderTheyArriveIn()
    {
        CodexLiveResult result = await ExchangeAsync(
            UsageReply,
            """{"jsonrpc":"2.0","method":"log","params":{"level":"debug"}}""",
            InitializeReply,
            RateLimitsReply);

        Assert.Equal(CodexLiveOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, result.RateLimits?.Windows.Count);
        Assert.Equal(987_654_321L, result.AccountUsage?.LifetimeTokens);
    }

    [Fact]
    public async Task AnErrorOnInitializeFailsTheCallEvenWhenAQuotaReplyArrived()
    {
        // A refusal here is the normal answer under API-key authentication. Taking the
        // quota reply anyway would present whatever came back from a session the server had
        // just declined to open.
        CodexLiveResult result = await ExchangeAsync(
            """{"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"unsupported auth mode"}}""",
            RateLimitsReply,
            UsageReply);

        Assert.Equal(CodexLiveOutcome.Failed, result.Outcome);
        Assert.Null(result.RateLimits);
    }

    [Fact]
    public async Task AnErrorOnTheQuotaReadIsAFailureThatStillCarriesTheAccountFigures()
    {
        // Two separate requests. One of them refusing does not throw the other's answer
        // away, and it does not promote the call to a success either.
        CodexLiveResult result = await ExchangeAsync(
            InitializeReply,
            """{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"rate limits unavailable"}}""",
            UsageReply);

        Assert.Equal(CodexLiveOutcome.Failed, result.Outcome);
        Assert.Null(result.RateLimits);
        Assert.Equal(987_654_321L, result.AccountUsage?.LifetimeTokens);
    }

    [Fact]
    public async Task AQuotaReplyStandsOnItsOwnWhenTheUsageReadAnswersWithNothingUsable()
    {
        CodexLiveResult result = await ExchangeAsync(
            InitializeReply,
            RateLimitsReply,
            """{"jsonrpc":"2.0","id":3,"result":{"summary":{}}}""");

        Assert.Equal(CodexLiveOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, result.RateLimits?.Windows.Count);
        Assert.Null(result.AccountUsage);
    }

    [Fact]
    public async Task ACancelledExchangeSurfacesSoTheCallerCanReportATimeout()
    {
        // The deadline is a linked token source with a timer on it. The exchange does not
        // swallow the cancellation: the caller turns it into a timeout, kills the child and
        // falls back to the newest local snapshot with its age stated.
        using var deadline = new CancellationTokenSource();
        using var responses = new BlockingReader(deadline);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CodexAppServerClient.ExchangeAsync(
                new StringWriter(),
                responses,
                ClientName,
                ClientVersion,
                deadline.Token));

        Assert.True(responses.WasRead, "the exchange must have waited on the response stream");
    }

    [Fact]
    public async Task AMissingCliIsNotDetectedAndNothingIsStarted()
    {
        var client = new CodexAppServerClient("altim-no-such-cli-ffffffff");

        Assert.False(client.IsAvailable);
        Assert.Equal(
            CodexLiveOutcome.NotDetected,
            (await client.ReadAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task ATimeoutThatHasAlreadyElapsedIsRejectedRatherThanMeaningNoLimit() =>
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new CodexAppServerClient("codex").ReadAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));

    [Fact]
    public async Task AChildThatAnswersIsKilledRatherThanLeftRunning()
    {
        // The real app-server does not exit once it has answered. Every exit path from the
        // call kills the process tree, so a monitor refreshing all day does not leave a
        // trail of live CLI processes behind it.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The fake app-server is a batch script.");

        using var workspace = new TempWorkspace();
        string script = WriteFakeAppServer(
            workspace,
            "answers.cmd",
            "echo " + InitializeReply,
            """echo {"jsonrpc":"2.0","id":2,"result":{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":73,"windowDurationMins":300,"resetsAt":1789515600}}}}}""",
            """echo {"jsonrpc":"2.0","id":3,"result":{"summary":{"lifetimeTokens":987654321}}}""",

            // And then it stays up, as the real one does.
            "ping -n 90 127.0.0.1 >nul");

        CodexLiveResult result = await new CodexAppServerClient(script)
            .ReadAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(CodexLiveOutcome.Succeeded, result.Outcome);
        CodexLimitWindow window = Assert.Single(result.RateLimits!.Windows);
        Assert.Equal(300L, window.WindowMinutes);
        Assert.Equal(73d, window.UsedPercent);
        Assert.Equal(987_654_321L, result.AccountUsage?.LifetimeTokens);

        AssertChildIsGone(workspace);
    }

    [Fact]
    public async Task AChildThatTalksWithoutAnsweringTimesOutAndIsKilled()
    {
        // The failure that would otherwise hang a refresh: a CLI that is alive and chatty
        // and never produces the reply. The deadline ends the exchange, the outcome says
        // "timed out" rather than "failed" — the two mean different things to the status
        // line — and the process does not outlive the call.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The fake app-server is a batch script.");

        using var workspace = new TempWorkspace();
        string script = WriteFakeAppServer(
            workspace,
            "chatty.cmd",
            "echo starting up, please wait",
            "ping -n 3 127.0.0.1 >nul",
            "echo still starting up",
            "ping -n 3 127.0.0.1 >nul",
            "echo nearly ready",
            "ping -n 3 127.0.0.1 >nul",
            "echo any moment now",
            "ping -n 90 127.0.0.1 >nul");

        var stopwatch = Stopwatch.StartNew();
        CodexLiveResult result = await new CodexAppServerClient(script)
            .ReadAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal(CodexLiveOutcome.TimedOut, result.Outcome);
        Assert.Null(result.RateLimits);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            "the deadline must end the call rather than the child's own lifetime, but it took "
            + stopwatch.Elapsed.ToString("c", CultureInfo.InvariantCulture));

        AssertChildIsGone(workspace);
    }

    private static string Describe(CodexLimitWindow window) =>
        FormattableString.Invariant($"{window.LimitId}:{window.WindowMinutes}:{window.UsedPercent}");

    private static string Method(JsonDocument document) =>
        document.RootElement.GetProperty("method").GetString() ?? string.Empty;

    private static string Stream(params string[] lines) => string.Join("\n", lines) + "\n";

    private static Task<CodexLiveResult> ExchangeAsync(params string[] responses) =>
        CodexAppServerClient.ExchangeAsync(
            new StringWriter(),
            new StringReader(Stream(responses)),
            ClientName,
            ClientVersion,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Writes a batch script that behaves like <c>codex app-server</c>.
    /// </summary>
    /// <remarks>
    /// The body runs inside a block whose standard error is redirected to a lock file, so
    /// that file is held open for exactly as long as the process tree lives. That is how
    /// <see cref="AssertChildIsGone"/> tells a killed child from one that is still running,
    /// without going near the process table — enumerating processes is the thing this
    /// codebase refuses to do.
    /// </remarks>
    private static string WriteFakeAppServer(TempWorkspace workspace, string name, params string[] body) =>
        workspace.WriteRaw(
            name,
            "@echo off\r\n(\r\n" + string.Join("\r\n", body) + "\r\n) 2> \"%~dp0" + LockFileName + "\"\r\n");

    private static void AssertChildIsGone(TempWorkspace workspace)
    {
        string lockPath = workspace.Path_(LockFileName);
        Assert.True(File.Exists(lockPath), "the fake app-server never started");

        // The child would hold this for another minute and a half. The handle is released
        // the moment the process tree dies, so a short poll means a kill and a long wait
        // means a leak.
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using FileStream held = File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }

        Assert.Fail("the child process outlived the call: " + LockFileName + " is still held open.");
    }

    /// <summary>
    /// A response stream that never produces a line and ends only when the deadline does.
    /// </summary>
    private sealed class BlockingReader : TextReader
    {
        private readonly CancellationTokenSource _deadline;

        public BlockingReader(CancellationTokenSource deadline) => _deadline = deadline;

        /// <summary>True once the exchange actually waited for a response line.</summary>
        public bool WasRead { get; private set; }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            WasRead = true;

            // The real deadline is a timer on a linked token source. Same shape, fired by
            // hand so the test does not wait on a clock.
            await _deadline.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return null;
        }
    }
}
