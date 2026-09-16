using Altim.Core.Models;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// A null is a null. These are the assertions that keep a missing reading from being
/// quietly rendered as zero.
/// </summary>
public sealed class UsageMetricTests
{
    [Fact]
    public void NullPercentIsNotReported()
    {
        var metric = new UsageMetric("five_hour", "Session", UsedPercent: null, Window: null,
            MetricConfidence.Documented);

        Assert.False(metric.IsUsedPercentReported);
        Assert.Null(metric.UsedPercent);
    }

    [Fact]
    public void ZeroPercentIsReported()
    {
        var metric = new UsageMetric("five_hour", "Session", UsedPercent: 0d, Window: null,
            MetricConfidence.Documented);

        Assert.True(metric.IsUsedPercentReported);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(42.5d)]
    [InlineData(100d)]
    [InlineData(101d)]
    public void PlausiblePercentsAreReported(double used)
    {
        var metric = new UsageMetric("seven_day", "Weekly", used, Window: null, MetricConfidence.BestEffort);

        Assert.True(metric.IsUsedPercentReported);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(101.5d)]
    [InlineData(1_763_000_000d)] // the known defect: a Unix timestamp in the percent field
    public void ImplausiblePercentsAreNotReported(double used)
    {
        var metric = new UsageMetric("seven_day", "Weekly", used, Window: null, MetricConfidence.BestEffort);

        Assert.False(metric.IsUsedPercentReported);
    }

    [Fact]
    public void UnreportedWindowLeavesResetUnknown()
    {
        var usage = new ProviderUsage(
            "claude",
            ProviderStatus.Error,
            [new UsageMetric("five_hour", "Session", null, null, MetricConfidence.Documented)],
            Tokens: null,
            LastRefreshed: null,
            StatusDetail: "Unable to retrieve usage");

        UsageMetric only = Assert.Single(usage.Metrics);

        Assert.Null(only.Window);
        Assert.Null(usage.Tokens);
        Assert.Null(usage.LastRefreshed);
        Assert.Equal("Unable to retrieve usage", usage.StatusDetail);
    }

    [Fact]
    public void UnreportedTokenComponentsStayNull()
    {
        var totals = new TokenTotals(Input: 1_200, Output: null, CacheRead: null, CacheWrite: 0);

        Assert.Equal<long?>(1_200L, totals.Input);
        Assert.Null(totals.Output);
        Assert.Null(totals.CacheRead);
        Assert.Equal<long?>(0L, totals.CacheWrite);
    }

    [Fact]
    public void LimitWindowWithoutResetInstantReportsNull()
    {
        var window = new LimitWindow(TimeSpan.FromHours(5), ResetsAt: null);

        Assert.Equal(TimeSpan.FromMinutes(300), window.Length);
        Assert.Null(window.ResetsAt);
    }
}
