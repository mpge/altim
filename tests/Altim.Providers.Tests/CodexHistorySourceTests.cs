using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Providers.Cli;
using Altim.Providers.Codex;
using Altim.Providers.Codex.AppServer;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// Backfilling the usage map from the daily buckets <c>account/usage/read</c> returns.
/// </summary>
/// <remarks>
/// <para>
/// Every exchange here runs over the same fake stdio pair
/// <see cref="CodexAppServerClientTests"/> uses: a <see cref="StringWriter"/> for the
/// requests and a <see cref="StringReader"/> for the replies. The real CLI is never
/// started. Asking it would have it call OpenAI with the user's own stored credentials and
/// spend their quota, which a test is not entitled to do.
/// </para>
/// <para>
/// The bucket field names are inferred rather than documented, so the tests that matter
/// most are the ones about a shape this reader does not recognise: it has to read as
/// <b>no backfill</b> — days that stay unknown — and never as a row of zeroes.
/// </para>
/// </remarks>
public sealed class CodexHistorySourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 17);

    private const string InitializeReply = """{"jsonrpc":"2.0","id":1,"result":{"userAgent":"codex/0.153.4"}}""";

    private const string RateLimitsReply =
        """
        {"jsonrpc":"2.0","id":2,"result":{"rateLimitsByLimitId":{"codex":{"planType":"pro","primary":{"usedPercent":73,"windowDurationMins":300,"resetsAt":1789515600}}}}}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryDailyBucketBecomesOneBackfilledDayOldestFirst()
    {
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            """{"dailyUsageBuckets":[{"startDate":"2026-09-16","tokens":2000},{"startDate":"2026-09-14","tokens":1000},{"startDate":"2026-09-15","tokens":3000}]}""");

        Assert.Equal(
            new DateOnly[] { new(2026, 9, 14), new(2026, 9, 15), new(2026, 9, 16) },
            days.Select(static day => day.Day).ToArray());

        Assert.All(days, static day => Assert.Equal(UsageDaySource.Backfilled, day.Source));
        Assert.All(days, static day => Assert.Equal(CodexProviderInfo.Id, day.ProviderId));
        Assert.All(days, static day => Assert.Equal(Now, day.UpdatedAt));
        Assert.Equal(new long?[] { 1000L, 3000L, 2000L }, days.Select(static day => day.TotalTokens).ToArray());
    }

    [Fact]
    public async Task TheOneFigureTheReplyGivesIsNotSpreadAcrossComponentsItNeverReported()
    {
        // The reply carries one undifferentiated number per day. Splitting it into input,
        // output and cache — even "half and half" — would be Altim inventing a breakdown
        // the provider did not report, which is the one thing this product refuses to do.
        UsageDay day = Assert.Single(
            await BackfillAsync("""{"dailyUsageBuckets":[{"startDate":"2026-09-15","tokens":4242}]}"""));

        Assert.NotNull(day.Tokens);
        Assert.Equal(4242L, day.Tokens.Input);
        Assert.Null(day.Tokens.Output);
        Assert.Null(day.Tokens.CacheRead);
        Assert.Null(day.Tokens.CacheWrite);
        Assert.Equal(4242L, day.TotalTokens);
    }

    [Fact]
    public async Task TheSnakeCaseSpellingsAreAcceptedTheSameWayEverywhereElseInTheClient()
    {
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            """{"daily_usage_buckets":[{"start_date":"2026-09-15","total_tokens":1234}]}""");

        Assert.Equal(1234L, Assert.Single(days).TotalTokens);
    }

    [Fact]
    public async Task TheTwoSpellingsOfOneConceptCanBeMixedWithinTheSameReply()
    {
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            """{"dailyUsageBuckets":[{"start_date":"2026-09-15","tokens":10},{"startDate":"2026-09-16","total_tokens":20}]}""");

        Assert.Equal(new long?[] { 10L, 20L }, days.Select(static day => day.TotalTokens).ToArray());
    }

    [Fact]
    public async Task AReplyWithNoBucketArrayBackfillsNothingRatherThanZeroes()
    {
        // No array at all. Every day in the range has to stay unknown; a run of zeroes
        // would claim the account used nothing for a fortnight.
        Assert.Empty(await BackfillAsync("""{"summary":{"lifetimeTokens":987654321}}"""));
    }

    [Fact]
    public async Task ABucketArrayInAShapeThisReaderDoesNotKnowBackfillsNothingRatherThanZeroes()
    {
        // The field names were inferred, not documented. If OpenAI renames them the array
        // still arrives, still has a length, and carries nothing this reader can use. That
        // has to read as "no backfill", not as a fortnight of empty days.
        Assert.Empty(await BackfillAsync("""{"dailyUsageBuckets":[{"day":1},{"day":2},{"day":3}]}"""));
    }

    [Fact]
    public async Task ABucketArrayThatIsNotAnArrayIsNotACrash()
    {
        Assert.Empty(await BackfillAsync("""{"dailyUsageBuckets":{"startDate":"2026-09-15","tokens":10}}"""));
    }

    [Fact]
    public async Task AMalformedDateIsSkippedRatherThanDefaultedToToday()
    {
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            """
            {"dailyUsageBuckets":[
              {"startDate":"2026-09-15","tokens":10},
              {"startDate":"yesterday","tokens":20},
              {"startDate":"2026-13-45","tokens":30},
              {"startDate":"2026-09","tokens":40},
              {"startDate":12345,"tokens":50},
              {"tokens":60}
            ]}
            """.ReplaceLineEndings(string.Empty));

        UsageDay day = Assert.Single(days);
        Assert.Equal(new DateOnly(2026, 9, 15), day.Day);

        // The date Altim would have reached for if it defaulted anything.
        Assert.DoesNotContain(days, static candidate => candidate.Day == DateOnly.FromDateTime(Now.Date));
    }

    [Fact]
    public async Task ABucketWithNoTokenFigureIsAbsentRatherThanZero()
    {
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            """{"dailyUsageBuckets":[{"startDate":"2026-09-15"},{"startDate":"2026-09-16","tokens":-1},{"startDate":"2026-09-17","tokens":0}]}""");

        // A day the provider reported as zero is a reading and is kept. A day it did not
        // account for at all is absent, so the square stays unknown.
        UsageDay day = Assert.Single(days);
        Assert.Equal(new DateOnly(2026, 9, 17), day.Day);
        Assert.Equal(0L, day.TotalTokens);
    }

    [Fact]
    public async Task DaysOutsideTheRequestedRangeAreNotReturned()
    {
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            """{"dailyUsageBuckets":[{"startDate":"2026-08-31","tokens":10},{"startDate":"2026-09-01","tokens":20},{"startDate":"2026-09-17","tokens":30},{"startDate":"2026-09-18","tokens":40}]}""");

        Assert.Equal(
            new DateOnly[] { new(2026, 9, 1), new(2026, 9, 17) },
            days.Select(static day => day.Day).ToArray());
    }

    [Fact]
    public async Task ADayTheReplyReportsTwiceIsSummedRatherThanSilentlyHalfDropped()
    {
        // One row per provider per day, so a repeated date has to resolve to one figure.
        // Both numbers were reported, so both are counted; keeping one and discarding the
        // other would lose a figure the provider did give.
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            """{"dailyUsageBuckets":[{"startDate":"2026-09-15","tokens":10},{"startDate":"2026-09-15","tokens":32}]}""");

        Assert.Equal(42L, Assert.Single(days).TotalTokens);
    }

    [Fact]
    public async Task NoDayCarriesAPeakPercentBecauseTheReplyReportsTokensAndNotQuota()
    {
        // The daily buckets are token counts. There is no percentage in them, and deriving
        // one from peakDailyTokens would put a number Altim computed into a field that says
        // the provider reported it.
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            """{"summary":{"peakDailyTokens":5000000},"dailyUsageBuckets":[{"startDate":"2026-09-15","tokens":10}]}""");

        Assert.Null(Assert.Single(days).PeakPercent);
    }

    [Fact]
    public async Task AnInvertedRangeAsksForNothingAndReturnsNothing()
    {
        var client = new StdioAppServerClient(
            InitializeReply,
            RateLimitsReply,
            UsageReply("""{"dailyUsageBuckets":[{"startDate":"2026-09-15","tokens":10}]}"""));

        using var workspace = new TempWorkspace();
        using CodexUsageProvider provider = CreateProvider(client, workspace.Root);

        Assert.Empty(await ((IUsageHistorySource)provider).GetHistoryAsync(To, From, Ct));
        Assert.Equal(0, client.ExchangeCount);
    }

    [Fact]
    public async Task WithNetworkPermissionOffNothingIsBackfilledAndNoProcessIsStarted()
    {
        // Not "the code path looks right": both seams that can start a process are booby
        // trapped, so an attempt to reach the CLI fails the test rather than passing it.
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets))
        {
            MustNotBeCalled = true,
        };
        var runner = new TripwireCliRunner();

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            runner,
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            networkPolicy: new MutableNetworkPolicy { AllowsNetworkCalls = false });

        Assert.Empty(await ((IUsageHistorySource)provider).GetHistoryAsync(From, To, Ct));

        Assert.Equal(0, client.ExchangeCount);
        Assert.Equal(0, runner.RunCount);
    }

    [Fact]
    public async Task TheConstructedOptionAlsoStopsTheBackfillOnItsOwn()
    {
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets))
        {
            MustNotBeCalled = true,
        };
        var runner = new TripwireCliRunner();

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default with { AllowNetworkCalls = false },
            client,
            runner,
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            networkPolicy: new MutableNetworkPolicy { AllowsNetworkCalls = true });

        Assert.Empty(await ((IUsageHistorySource)provider).GetHistoryAsync(From, To, Ct));

        Assert.Equal(0, client.ExchangeCount);
        Assert.Equal(0, runner.RunCount);
    }

    [Fact]
    public async Task TheBackfillGoesThroughTheSameRefreshGateAsTheLiveQuotaCall()
    {
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets));
        var gate = new SwitchableRefreshGate { IsOpen = false };

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            gate);

        var source = (IUsageHistorySource)provider;

        Assert.Empty(await source.GetHistoryAsync(From, To, Ct));
        Assert.Equal(0, client.ExchangeCount);
        Assert.Equal(CodexProviderInfo.Id, Assert.Single(gate.Requested));

        gate.IsOpen = true;
        Assert.NotEmpty(await source.GetHistoryAsync(From, To, Ct));
        Assert.Equal(1, client.ExchangeCount);
    }

    [Fact]
    public async Task AMissingCliBackfillsNothingAndAsksNobody()
    {
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets))
        {
            IsAvailable = false,
            MustNotBeCalled = true,
        };

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            new TripwireCliRunner(),
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now));

        Assert.Empty(await ((IUsageHistorySource)provider).GetHistoryAsync(From, To, Ct));
        Assert.Equal(0, client.ExchangeCount);
    }

    [Fact]
    public async Task ARefusedExchangeBackfillsNothingRatherThanZeroes()
    {
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            InitializeReply,
            RateLimitsReply,
            """{"jsonrpc":"2.0","id":3,"error":{"code":-32000,"message":"usage unavailable"}}""");

        Assert.Empty(days);
    }

    [Fact]
    public async Task TheUsageHalfStillBackfillsWhenTheQuotaHalfIsRefused()
    {
        // Two separate requests. One of them failing does not throw the other's answer
        // away, and the history the usage read did return is still worth keeping.
        IReadOnlyList<UsageDay> days = await BackfillAsync(
            InitializeReply,
            """{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"rate limits unavailable"}}""",
            UsageReply(Buckets));

        Assert.Equal(2, days.Count);
    }

    [Fact]
    public async Task TheRequestSentIsTheDocumentedAccountUsageReadAndNothingElse()
    {
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets));

        using var workspace = new TempWorkspace();
        using CodexUsageProvider provider = CreateProvider(client, workspace.Root);

        _ = await ((IUsageHistorySource)provider).GetHistoryAsync(From, To, Ct);

        Assert.Contains("account/usage/read", client.Requests, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClosedGateFallsBackToTheReadingTheLiveMeterAlreadyTook()
    {
        // The defect this test exists for. On the verification machine the scheduler
        // refreshes every 60 seconds and the gate opens every 60 seconds, so the live read
        // takes the gate within seconds of it opening; the backfill, which asks every five
        // minutes at an arbitrary instant, essentially never found it open. Measured
        // result: maintenance.last_backfill.claude was stamped, no
        // maintenance.last_backfill.codex key existed at all, and the Codex row of the map
        // would have stayed unknown for good.
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets));
        var gate = new SwitchableRefreshGate { IsOpen = true };

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            gate);

        // The scheduler's live read, which is what spends the minute's one call.
        await provider.RefreshAsync(Ct);
        Assert.Equal(1, client.ExchangeCount);

        gate.IsOpen = false;
        IReadOnlyList<UsageDay> days = await ((IUsageHistorySource)provider).GetHistoryAsync(From, To, Ct);

        // Non-empty is the load-bearing claim rather than a detail of it: AltimRuntime
        // counts a backfill as having run only when the list has something in it, so a
        // fallback that came back empty would be retried for ever exactly as the skip was.
        Assert.Equal(
            new DateOnly[] { new(2026, 9, 15), new(2026, 9, 16) },
            days.Select(static day => day.Day).ToArray());
        Assert.Equal(new long?[] { 1000L, 2000L }, days.Select(static day => day.TotalTokens).ToArray());
        Assert.All(days, static day => Assert.Equal(UsageDaySource.Backfilled, day.Source));

        // The gate was shut and stayed shut: no second call reached the network.
        Assert.Equal(1, client.ExchangeCount);
    }

    [Fact]
    public async Task AClosedGateWithNothingRememberedStillBackfillsNothing()
    {
        // The other half of the rule. A fallback with nothing to fall back to has nothing
        // to say, and the days stay unknown rather than becoming a row of zeroes.
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets))
        {
            MustNotBeCalled = true,
        };
        var gate = new SwitchableRefreshGate { IsOpen = false };

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            gate);

        Assert.Empty(await ((IUsageHistorySource)provider).GetHistoryAsync(From, To, Ct));
        Assert.Equal(0, client.ExchangeCount);
    }

    [Fact]
    public async Task AFreshLiveReadingIsPreferredOverTheOneAlreadyInHand()
    {
        // The remembered reading is a fallback, never a cache. When the call does go
        // through, what the server has just said wins outright — including when it
        // accounts for fewer days than the reading already in hand, because the server
        // restating its own history is not Altim losing days.
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets));
        client.Then(
            InitializeReply,
            RateLimitsReply,
            UsageReply("""{"dailyUsageBuckets":[{"startDate":"2026-09-17","tokens":7}]}"""));

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            new SwitchableRefreshGate { IsOpen = true });

        var source = (IUsageHistorySource)provider;
        Assert.Equal(2, (await source.GetHistoryAsync(From, To, Ct)).Count);

        UsageDay day = Assert.Single(await source.GetHistoryAsync(From, To, Ct));
        Assert.Equal(new DateOnly(2026, 9, 17), day.Day);
        Assert.Equal(7L, day.TotalTokens);
        Assert.Equal(2, client.ExchangeCount);
    }

    [Fact]
    public async Task ARememberedReadingIsStillUsedAtTheEdgeOfItsRetention()
    {
        // The bound is the one Choose already applies to a remembered quota snapshot, so
        // "how long a live reading keeps answering" means one thing in this file.
        IReadOnlyList<UsageDay> days = await FallBackAfterAsync(CodexOptions.Default.LiveSnapshotRetention);

        Assert.Equal(2, days.Count);
    }

    [Fact]
    public async Task ARememberedReadingPastItsRetentionIsNotUsed()
    {
        // ServerReportedUsageAt cannot outlive the process, but a machine left running for
        // a week can. Today's bucket must never be backfilled from a reading old enough to
        // be about a different day, so past the bound the reading stops answering and the
        // day goes back to unknown.
        Assert.Empty(await FallBackAfterAsync(
            CodexOptions.Default.LiveSnapshotRetention + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task TheFallbackStartsNoProcessOfItsOwn()
    {
        // Not "the code path looks right": both seams that can start a process are booby
        // trapped for the second call, so a fallback that quietly reached for the CLI
        // fails the test rather than passing it. The first call is let through because it
        // is the reading being fallen back to.
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets));
        var runner = new TripwireCliRunner { Armed = false };
        var gate = new SwitchableRefreshGate { IsOpen = true };

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            runner,
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            gate);

        var source = (IUsageHistorySource)provider;
        Assert.NotEmpty(await source.GetHistoryAsync(From, To, Ct));

        gate.IsOpen = false;
        client.MustNotBeCalled = true;
        runner.Armed = true;

        Assert.NotEmpty(await source.GetHistoryAsync(From, To, Ct));
        Assert.Equal(1, client.ExchangeCount);
        Assert.Equal(1, runner.RunCount);
    }

    [Fact]
    public async Task TheFallbackReportsOnlyTheDaysItWasGivenAndInventsNoneBetweenThem()
    {
        // A fallback is allowed to be a few minutes old. It is not allowed to grow a
        // bucket the provider never gave: a day between two reported ones stays absent,
        // and a day outside the requested range is dropped the way a live answer's is.
        var client = new StdioAppServerClient(
            InitializeReply,
            RateLimitsReply,
            UsageReply("""{"dailyUsageBuckets":[{"startDate":"2026-08-31","tokens":10},{"startDate":"2026-09-01","tokens":20},{"startDate":"2026-09-16","tokens":30}]}"""));
        var gate = new SwitchableRefreshGate { IsOpen = true };

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            gate);

        var source = (IUsageHistorySource)provider;
        Assert.NotEmpty(await source.GetHistoryAsync(From, To, Ct));

        gate.IsOpen = false;
        client.MustNotBeCalled = true;

        IReadOnlyList<UsageDay> days = await source.GetHistoryAsync(From, To, Ct);

        Assert.Equal(
            new DateOnly[] { new(2026, 9, 1), new(2026, 9, 16) },
            days.Select(static day => day.Day).ToArray());
        Assert.Equal(new long?[] { 20L, 30L }, days.Select(static day => day.TotalTokens).ToArray());
    }

    [Fact]
    public async Task ARememberedReadingThatAccountsForNoDayFallsBackToNothing()
    {
        // A successful exchange whose usage half carried only a lifetime total is a real
        // reading and is remembered, but it accounts for no calendar day. The backfill
        // still has nothing to hand back, so the caller leaves the stamp alone and the
        // next pass asks again.
        var client = new StdioAppServerClient(
            InitializeReply,
            RateLimitsReply,
            UsageReply("""{"summary":{"lifetimeTokens":987654321}}"""));
        var gate = new SwitchableRefreshGate { IsOpen = true };

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            gate);

        var source = (IUsageHistorySource)provider;
        Assert.Empty(await source.GetHistoryAsync(From, To, Ct));

        gate.IsOpen = false;
        client.MustNotBeCalled = true;

        Assert.Empty(await source.GetHistoryAsync(From, To, Ct));
    }

    [Fact]
    public async Task SwitchingNetworkCallsOffForgetsTheRememberedReadingRatherThanFallingBackToIt()
    {
        // Local-only means local-only. The remembered reading came from the vendor, and a
        // fallback that went on serving it after the user switched the permission off
        // would be the setting having no effect on the thing it names.
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets));
        var policy = new MutableNetworkPolicy { AllowsNetworkCalls = true };

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now),
            new SwitchableRefreshGate { IsOpen = true },
            policy);

        var source = (IUsageHistorySource)provider;
        Assert.NotEmpty(await source.GetHistoryAsync(From, To, Ct));

        policy.AllowsNetworkCalls = false;
        client.MustNotBeCalled = true;

        Assert.Empty(await source.GetHistoryAsync(From, To, Ct));
    }

    /// <summary>
    /// Takes one live reading, moves the clock on by <paramref name="elapsed"/>, shuts the
    /// gate and asks for history again.
    /// </summary>
    private static async Task<IReadOnlyList<UsageDay>> FallBackAfterAsync(TimeSpan elapsed)
    {
        var client = new StdioAppServerClient(InitializeReply, RateLimitsReply, UsageReply(Buckets));
        var gate = new SwitchableRefreshGate { IsOpen = true };
        var clock = new MovableTimeProvider(Now);

        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            client,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            clock,
            gate);

        var source = (IUsageHistorySource)provider;
        Assert.NotEmpty(await source.GetHistoryAsync(From, To, Ct));

        clock.Advance(elapsed);
        gate.IsOpen = false;
        client.MustNotBeCalled = true;

        return await source.GetHistoryAsync(From, To, Ct);
    }

    private const string Buckets =
        """{"dailyUsageBuckets":[{"startDate":"2026-09-15","tokens":1000},{"startDate":"2026-09-16","tokens":2000}]}""";

    private static string UsageReply(string result) =>
        """{"jsonrpc":"2.0","id":3,"result":""" + result + "}";

    private static CodexUsageProvider CreateProvider(StdioAppServerClient client, string home) =>
        new(
            CodexOptions.Default,
            client,
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            home,
            new FixedTimeProvider(Now));

    private static Task<IReadOnlyList<UsageDay>> BackfillAsync(string usageResult) =>
        BackfillAsync(InitializeReply, RateLimitsReply, UsageReply(usageResult));

    private static async Task<IReadOnlyList<UsageDay>> BackfillAsync(params string[] replies)
    {
        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            new StdioAppServerClient(replies),
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now));

        return await ((IUsageHistorySource)provider).GetHistoryAsync(From, To, Ct);
    }

    /// <summary>
    /// An app-server client that runs the real exchange over a fake pair of streams.
    /// </summary>
    /// <remarks>
    /// The parsing under test is the production parser: this substitutes the process, not
    /// the protocol. <see cref="MustNotBeCalled"/> turns the client into a tripwire for the
    /// tests whose whole claim is that no process was started.
    /// </remarks>
    private sealed class StdioAppServerClient : ICodexAppServerClient
    {
        private readonly Queue<string> _queued = new();
        private string _responses;

        public StdioAppServerClient(params string[] responses) =>
            _responses = Script(responses);

        /// <summary>
        /// Queues the replies the next exchange answers with, so a test can have the
        /// server say something different the second time it is asked.
        /// </summary>
        public void Then(params string[] responses) => _queued.Enqueue(Script(responses));

        /// <summary>Whether the CLI is considered installed.</summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>Fails the call outright, so a forbidden call cannot pass quietly.</summary>
        public bool MustNotBeCalled { get; set; }

        /// <summary>How many exchanges were actually run.</summary>
        public int ExchangeCount { get; private set; }

        /// <summary>Everything written to the request stream of the last exchange.</summary>
        public string Requests { get; private set; } = string.Empty;

        public async Task<CodexLiveResult> ReadAsync(TimeSpan timeout, CancellationToken ct)
        {
            Assert.False(MustNotBeCalled, "the Codex app-server must not be started for this call.");

            ExchangeCount++;

            string script = _responses;
            if (_queued.Count > 0)
            {
                // Answer with the current script, then move on to the one a test
                // queued for the next exchange. The last script keeps answering.
                _responses = _queued.Dequeue();
            }

            var requests = new StringWriter();
            CodexLiveResult result = await CodexAppServerClient.ExchangeAsync(
                requests,
                new StringReader(script),
                "altim",
                "1.0",
                ct);

            Requests = requests.ToString();
            return result;
        }

        private static string Script(string[] responses) => string.Join("\n", responses) + "\n";
    }

    /// <summary>
    /// A CLI runner that fails the test if anything tries to start a process through it.
    /// </summary>
    /// <remarks>
    /// <c>codex doctor --json</c> is the other process the provider can start, and a
    /// permission check that ran after it would already have spent a process start by the
    /// time it decided not to.
    /// </remarks>
    private sealed class TripwireCliRunner : ICliRunner
    {
        /// <summary>How many processes this runner was asked to start.</summary>
        public int RunCount { get; private set; }

        /// <summary>
        /// Whether a run fails the test. A test that needs one legitimate process start
        /// before the forbidden one arms the tripwire in between, so the run it is
        /// really about is the only one that can trip it.
        /// </summary>
        public bool Armed { get; set; } = true;

        public bool Exists(string command) => true;

        public Task<CliRunResult> RunAsync(
            string command,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            RunCount++;

            if (Armed)
            {
                Assert.Fail("no process may be started for this call, and " + command + " was.");
            }

            return Task.FromResult(CliRunResult.Failed);
        }
    }
}
