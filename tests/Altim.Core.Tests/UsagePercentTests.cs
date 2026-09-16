using Altim.Core.Models;
using Altim.Core.Usage;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Percentage normalisation, including the provider defect that puts a Unix timestamp in
/// the percentage field.
/// </summary>
public sealed class UsagePercentTests
{
    [Fact]
    public void NullStaysNull()
    {
        Assert.Null(UsagePercent.Normalise((double?)null));
        Assert.False(UsagePercent.IsReported(null));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.5d)]
    [InlineData(42.5d)]
    [InlineData(99.9d)]
    [InlineData(100d)]
    public void PlausibleValuesPassThroughUnchanged(double raw)
    {
        Assert.Equal<double?>(raw, UsagePercent.Normalise(raw));
        Assert.True(UsagePercent.IsReported(raw));
    }

    [Theory]
    [InlineData(100.000_1d)]
    [InlineData(100.5d)]
    [InlineData(101d)]
    public void SlackAboveOneHundredClampsToOneHundred(double raw) =>
        Assert.Equal<double?>(100d, UsagePercent.Normalise(raw));

    [Theory]
    [InlineData(101.000_1d)]
    [InlineData(150d)]
    [InlineData(1_763_000_000d)] // the known defect: a Unix timestamp in the percentage field
    [InlineData(1_789_515_600d)]
    public void AboveTheSlackIsUnavailableNotClamped(double raw)
    {
        Assert.Null(UsagePercent.Normalise(raw));
        Assert.False(UsagePercent.IsReported(raw));
    }

    [Theory]
    [InlineData(-0.000_1d)]
    [InlineData(-1d)]
    [InlineData(-100d)]
    public void NegativeIsUnavailable(double raw) => Assert.Null(UsagePercent.Normalise(raw));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteIsUnavailable(double raw) => Assert.Null(UsagePercent.Normalise(raw));

    [Fact]
    public void UnavailableIsNeverZero()
    {
        double? normalised = UsagePercent.Normalise(1_763_000_000d);

        Assert.Null(normalised);
        Assert.NotEqual<double?>(0d, normalised);
    }

    [Fact]
    public void NormalisingAMetricClampsOnlyThePercentage()
    {
        var window = new LimitWindow(TimeSpan.FromMinutes(300), DateTimeOffset.UnixEpoch);
        var metric = new UsageMetric("five_hour", "Session", 100.6d, window, MetricConfidence.Documented);

        UsageMetric normalised = UsagePercent.Normalise(metric);

        Assert.Equal<double?>(100d, normalised.UsedPercent);
        Assert.Equal("five_hour", normalised.Key);
        Assert.Equal("Session", normalised.Label);
        Assert.Same(window, normalised.Window);
        Assert.Equal(MetricConfidence.Documented, normalised.Confidence);
    }

    [Fact]
    public void NormalisingAMetricTurnsTheDefectIntoNull()
    {
        var metric = new UsageMetric("seven_day", "Weekly", 1_789_549_200d, null, MetricConfidence.BestEffort);

        UsageMetric normalised = UsagePercent.Normalise(metric);

        Assert.Null(normalised.UsedPercent);
        Assert.False(normalised.IsUsedPercentReported);
    }

    [Fact]
    public void NormalisingAnAlreadyCleanMetricReturnsTheSameInstance()
    {
        var metric = new UsageMetric("five_hour", "Session", 53d, null, MetricConfidence.Documented);

        Assert.Same(metric, UsagePercent.Normalise(metric));
    }

    [Fact]
    public void AMetricWithNoReadingIsLeftAlone()
    {
        var metric = new UsageMetric("five_hour", "Session", null, null, MetricConfidence.Documented);

        Assert.Same(metric, UsagePercent.Normalise(metric));
    }
}
