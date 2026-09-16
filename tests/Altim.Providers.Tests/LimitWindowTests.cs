using Altim.Core.Models;
using Altim.Providers.Limits;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// Window classification and percentage plausibility: the two places a provider quirk turns
/// into a wrong number on screen.
/// </summary>
public sealed class LimitWindowTests
{
    [Theory]
    [InlineData(300, LimitWindowKind.FiveHour)]
    [InlineData(299, LimitWindowKind.FiveHour)]
    [InlineData(301, LimitWindowKind.FiveHour)]
    [InlineData(1440, LimitWindowKind.Daily)]
    [InlineData(1439, LimitWindowKind.Daily)]
    [InlineData(10080, LimitWindowKind.Weekly)]
    [InlineData(10079, LimitWindowKind.Weekly)]
    [InlineData(43200, LimitWindowKind.Monthly)]
    [InlineData(180, LimitWindowKind.Unknown)]
    [InlineData(0, LimitWindowKind.Unknown)]
    public void ClassifiesByLengthAndToleratesDrift(long minutes, LimitWindowKind expected) =>
        Assert.Equal(expected, LimitWindowClassifier.Classify(minutes));

    [Theory]
    [InlineData(299, 300)]
    [InlineData(10079, 10080)]
    [InlineData(10081, 10080)]
    [InlineData(180, 180)]
    public void NormalisesDriftedLengthsSoAKeyStaysStable(long reported, long expected) =>
        Assert.Equal(expected, LimitWindowClassifier.Normalize(reported));

    [Fact]
    public void AnOffByOneWeeklyWindowKeepsTheSameStorageKeyAsAnExactOne() =>
        Assert.Equal(LimitWindowClassifier.KeyFragment(10080), LimitWindowClassifier.KeyFragment(10079));

    [Theory]
    [InlineData(300, "5 hour")]
    [InlineData(299, "5 hour")]
    [InlineData(1440, "Daily")]
    [InlineData(10080, "Weekly")]
    [InlineData(43200, "Monthly")]
    [InlineData(180, "3 hour")]
    [InlineData(4320, "3 day")]
    [InlineData(7, "7 min")]
    public void LabelsUnknownWindowsByTheirOwnDurationRatherThanGuessing(long minutes, string expected) =>
        Assert.Equal(expected, LimitWindowClassifier.Label(minutes));

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(53d, 53d)]
    [InlineData(100d, 100d)]
    [InlineData(101d, 101d)]
    public void AcceptsPlausiblePercentages(double input, double expected) =>
        Assert.Equal(expected, PercentReading.Normalize(input));

    [Theory]
    [InlineData(101.5d)]
    [InlineData(120d)]
    [InlineData(1789515600d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsAnythingAboveTheCeilingRatherThanClampingIt(double input)
    {
        // The defect this covers writes an epoch timestamp into the percentage field before
        // a window has data. Clamping to 100 would turn that into a full meter and a
        // notification; discarding it makes the metric honestly unavailable.
        Assert.Null(PercentReading.Normalize(input));
    }

    [Fact]
    public void ARejectedPercentageMakesTheMetricUnreported()
    {
        var metric = new UsageMetric("five_hour", "Session", PercentReading.Normalize(1789515600d), null, MetricConfidence.Documented);

        Assert.Null(metric.UsedPercent);
        Assert.False(metric.IsUsedPercentReported);
    }

    [Fact]
    public void NullPercentIsNotZero()
    {
        var metric = new UsageMetric("seven_day", "Weekly", null, null, MetricConfidence.Documented);

        Assert.Null(metric.UsedPercent);
        Assert.False(metric.IsUsedPercentReported);
    }
}
