using Altim.Core.Models;
using Altim.Core.Usage;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Aggregation across providers that report different things, which is the normal case.
/// Nothing here may turn a null into a zero.
/// </summary>
public sealed class UsageAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoProvidersAggregatesToNothingReported()
    {
        UsageOverview overview = UsageAggregator.Aggregate([]);

        Assert.Same(UsageOverview.Empty, overview);
        Assert.Null(overview.WorstMetric);
        Assert.Null(overview.WorstUsedPercent);
        Assert.Null(overview.Tokens);
        Assert.Equal(ProviderStatus.Unknown, overview.Status);
        Assert.Equal("No providers configured", overview.StatusLine);
    }

    [Fact]
    public void TheWorstMetricWinsAcrossProviders()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", 53d), Metric("seven_day", "Weekly", 85d)]),
            Usage("codex", ProviderStatus.Idle, [Metric("codex:300", "Session", 91.5d)]),
        ]);

        Assert.Equal("codex", overview.WorstProviderId);
        Assert.Equal("codex:300", overview.WorstMetric?.Key);
        Assert.Equal<double?>(91.5d, overview.WorstUsedPercent);
        Assert.Equal(2, overview.ProviderCount);
    }

    [Fact]
    public void MetricsNobodyReportedAreSkippedRatherThanCountedAsZero()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", null), Metric("seven_day", "Weekly", 40d)]),
            Usage("codex", ProviderStatus.Idle, [Metric("codex:10080", "Weekly", null)]),
        ]);

        Assert.Equal<double?>(40d, overview.WorstUsedPercent);
        Assert.Equal("claude", overview.WorstProviderId);
    }

    [Fact]
    public void NothingReportedLeavesTheWorstMetricNull()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", null)]),
            Usage("codex", ProviderStatus.Idle, []),
        ]);

        Assert.Null(overview.WorstMetric);
        Assert.Null(overview.WorstUsedPercent);
        Assert.Equal("All providers operational", overview.StatusLine);
    }

    [Fact]
    public void TheTimestampDefectCannotBecomeTheWorstMetric()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active,
            [
                Metric("five_hour", "Session", 1_789_515_600d),
                Metric("seven_day", "Weekly", 12d),
            ]),
        ]);

        Assert.Equal("seven_day", overview.WorstMetric?.Key);
        Assert.Equal<double?>(12d, overview.WorstUsedPercent);
    }

    [Fact]
    public void TheWorstMetricIsReportedWithItsPercentageNormalised()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", 100.7d)]),
        ]);

        Assert.Equal<double?>(100d, overview.WorstUsedPercent);
    }

    [Fact]
    public void AFailedReadingContributesNoNumbers()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", 20d)]),
            Usage("codex", ProviderStatus.Error, [Metric("codex:300", "Session", 99d)],
                tokens: new TokenTotals(500, 500, 500, 500), detail: "Unable to retrieve usage"),
        ]);

        Assert.Equal<double?>(20d, overview.WorstUsedPercent);
        Assert.Equal("claude", overview.WorstProviderId);
        Assert.Null(overview.Tokens);
        Assert.Equal(ProviderStatus.Error, overview.Status);
        Assert.Equal("codex unavailable", overview.StatusLine);
    }

    [Fact]
    public void TokenComponentsAreSummedOnlyWhereReported()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, [], tokens: new TokenTotals(1_200, null, 40_000, null)),
            Usage("codex", ProviderStatus.Idle, [], tokens: new TokenTotals(800, 55, null, null)),
        ]);

        TokenTotals totals = Assert.IsType<TokenTotals>(overview.Tokens);
        Assert.Equal<long?>(2_000L, totals.Input);
        Assert.Equal<long?>(55L, totals.Output);
        Assert.Equal<long?>(40_000L, totals.CacheRead);
        Assert.Null(totals.CacheWrite);
    }

    [Fact]
    public void ASummedTotalIsNullWhenNothingReported()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, []),
            Usage("codex", ProviderStatus.Idle, [], tokens: new TokenTotals(null, null, null, null)),
        ]);

        Assert.Null(overview.Tokens);
    }

    [Fact]
    public void AZeroTokenCountIsKeptBecauseItWasReported()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, [], tokens: new TokenTotals(0, null, null, null)),
        ]);

        TokenTotals totals = Assert.IsType<TokenTotals>(overview.Tokens);
        Assert.Equal<long?>(0L, totals.Input);
        Assert.Null(totals.Output);
    }

    [Theory]
    [InlineData(ProviderStatus.Active, ProviderStatus.Idle, ProviderStatus.Active, "All providers operational")]
    [InlineData(ProviderStatus.Active, ProviderStatus.NotDetected, ProviderStatus.Active, "codex not detected")]
    [InlineData(ProviderStatus.Active, ProviderStatus.Unknown, ProviderStatus.Active, "Waiting for codex")]
    [InlineData(ProviderStatus.Idle, ProviderStatus.Unknown, ProviderStatus.Idle, "Waiting for codex")]
    [InlineData(ProviderStatus.Error, ProviderStatus.Error, ProviderStatus.Error, "2 providers unavailable")]
    [InlineData(ProviderStatus.Error, ProviderStatus.Active, ProviderStatus.Error, "claude unavailable")]
    [InlineData(ProviderStatus.Unknown, ProviderStatus.NotDetected, ProviderStatus.Unknown, "codex not detected")]
    [InlineData(ProviderStatus.NotDetected, ProviderStatus.NotDetected, ProviderStatus.NotDetected, "No providers detected")]
    public void TheOverallStatusIsTheOneWorthShowing(
        ProviderStatus first,
        ProviderStatus second,
        ProviderStatus expectedStatus,
        string expectedLine)
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", first, []),
            Usage("codex", second, []),
        ]);

        Assert.Equal(expectedStatus, overview.Status);
        Assert.Equal(expectedLine, overview.StatusLine);
    }

    [Fact]
    public void AnErrorOutranksEverythingElseInTheStatusLine()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.NotDetected, []),
            Usage("codex", ProviderStatus.Error, [], detail: "app-server did not respond"),
        ]);

        Assert.Equal(ProviderStatus.Error, overview.Status);
        Assert.Equal("codex unavailable", overview.StatusLine);
    }

    [Fact]
    public void WaitingForEverythingReadsAsWaiting()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Unknown, []),
            Usage("codex", ProviderStatus.Unknown, []),
        ]);

        Assert.Equal("Waiting for the first reading", overview.StatusLine);
    }

    [Fact]
    public void OneUninstalledProviderDoesNotOutrankAWorkingOne()
    {
        // The tray takes its colour from this status. A provider the user never installed
        // is a settled non-event and must not paint over one that is running right now.
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, [Metric("five_hour", "Session", 20d)]),
            Usage("codex", ProviderStatus.NotDetected, []),
        ]);

        Assert.Equal(ProviderStatus.Active, overview.Status);
        Assert.Equal("codex not detected", overview.StatusLine);
    }

    [Fact]
    public void AFailureStillOutranksAWorkingProvider()
    {
        UsageOverview overview = UsageAggregator.Aggregate(
        [
            Usage("claude", ProviderStatus.Active, []),
            Usage("codex", ProviderStatus.Error, [], detail: ProviderUsage.UnavailableDetail),
        ]);

        Assert.Equal(ProviderStatus.Error, overview.Status);
    }

    [Fact]
    public void TheStatusLineReadsWithDisplayNamesWhenItIsGivenThem()
    {
        ProviderUsage[] failing = [Usage("claude", ProviderStatus.Error, [], detail: ProviderUsage.UnavailableDetail)];
        ProviderUsage[] missing = [Usage("codex", ProviderStatus.NotDetected, []), Usage("claude", ProviderStatus.Active, [])];

        Assert.Equal("Claude Code unavailable", UsageAggregator.DescribeStatus(failing, DisplayName));
        Assert.Equal("Codex not detected", UsageAggregator.DescribeStatus(missing, DisplayName));
        Assert.Equal("Claude Code unavailable", UsageAggregator.Aggregate(failing, DisplayName).StatusLine);

        // Without a lookup, and for an id the lookup does not know, the id is all there is.
        Assert.Equal("claude unavailable", UsageAggregator.DescribeStatus(failing));
        Assert.Equal("gemini not detected", UsageAggregator.DescribeStatus(
            [Usage("gemini", ProviderStatus.NotDetected, []), Usage("claude", ProviderStatus.Active, [])], DisplayName));
    }

    private static string DisplayName(string providerId) => providerId switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        _ => providerId,
    };

    private static ProviderUsage Usage(
        string id,
        ProviderStatus status,
        IReadOnlyList<UsageMetric> metrics,
        TokenTotals? tokens = null,
        string? detail = null) =>
        new(id, status, metrics, tokens, Now, detail);

    private static UsageMetric Metric(string key, string label, double? percent) =>
        new(key, label, percent, new LimitWindow(TimeSpan.FromMinutes(300), Now.AddHours(2)), MetricConfidence.Documented);
}
