using Altim.Core.Models;
using Altim.Core.Usage;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Window classification by length. Slot names are never consulted, and off-by-one lengths
/// such as 299 and 10079 are real values seen in the wild.
/// </summary>
public sealed class LimitWindowClassifierTests
{
    [Theory]
    [InlineData(300d, LimitWindowKind.FiveHour)]
    [InlineData(299d, LimitWindowKind.FiveHour)]
    [InlineData(301d, LimitWindowKind.FiveHour)]
    [InlineData(1_440d, LimitWindowKind.Daily)]
    [InlineData(1_439d, LimitWindowKind.Daily)]
    [InlineData(10_080d, LimitWindowKind.Weekly)]
    [InlineData(10_079d, LimitWindowKind.Weekly)]
    [InlineData(43_200d, LimitWindowKind.Monthly)]
    [InlineData(43_199d, LimitWindowKind.Monthly)]
    public void KnownLengthsClassifyTolerantly(double minutes, LimitWindowKind expected) =>
        Assert.Equal(expected, LimitWindowClassifier.Classify(minutes));

    [Theory]
    [InlineData(40_320d)] // 28 days: February
    [InlineData(41_760d)] // 29 days: February in a leap year
    [InlineData(43_200d)] // 30 days
    [InlineData(44_640d)] // 31 days
    public void RealMonthLengthsClassifyAsMonthly(double minutes) =>
        Assert.Equal(LimitWindowKind.Monthly, LimitWindowClassifier.Classify(minutes));

    [Theory]
    [InlineData(1d)]
    [InlineData(60d)]
    [InlineData(360d)]
    [InlineData(2_880d)]
    [InlineData(20_160d)]
    public void UnknownLengthsClassifyAsOther(double minutes) =>
        Assert.Equal(LimitWindowKind.Other, LimitWindowClassifier.Classify(minutes));

    [Theory]
    [InlineData(0d)]
    [InlineData(-300d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonsenseLengthsClassifyAsOtherRatherThanThrow(double minutes) =>
        Assert.Equal(LimitWindowKind.Other, LimitWindowClassifier.Classify(minutes));

    [Fact]
    public void ATimeSpanClassifiesTheSameWayAsItsMinutes() =>
        Assert.Equal(LimitWindowKind.Weekly, LimitWindowClassifier.Classify(TimeSpan.FromMinutes(10_079)));

    [Fact]
    public void NoWindowClassifiesAsNullNotOther()
    {
        Assert.Null(LimitWindowClassifier.Classify((LimitWindow?)null));
        Assert.Null(LimitWindowClassifier.Label((LimitWindow?)null));
    }

    [Theory]
    [InlineData(LimitWindowKind.FiveHour, "Session")]
    [InlineData(LimitWindowKind.Daily, "Daily")]
    [InlineData(LimitWindowKind.Weekly, "Weekly")]
    [InlineData(LimitWindowKind.Monthly, "Monthly")]
    public void KnownFamiliesHaveFixedLabels(LimitWindowKind kind, string expected) =>
        Assert.Equal(expected, LimitWindowClassifier.Label(kind));

    [Fact]
    public void OtherHasNoFixedLabel() => Assert.Null(LimitWindowClassifier.Label(LimitWindowKind.Other));

    [Theory]
    [InlineData(300, "Session")]
    [InlineData(299, "Session")]
    [InlineData(10_080, "Weekly")]
    [InlineData(10_079, "Weekly")]
    [InlineData(1_440, "Daily")]
    [InlineData(43_200, "Monthly")]
    public void LabelsComeFromTheLengthNotTheSlot(int minutes, string expected) =>
        Assert.Equal(expected, LimitWindowClassifier.Label(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(45, "45 minute")]
    [InlineData(360, "6 hour")]
    [InlineData(90, "90 minute")]
    [InlineData(4_320, "3 day")]
    public void UnclassifiedLengthsAreDescribedFromTheirLength(int minutes, string expected) =>
        Assert.Equal(expected, LimitWindowClassifier.Label(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void AReportedWindowIsLabelledFromItsLength()
    {
        // The slot this arrived in was called "primary" in 2025-12 and "secondary" in
        // 2026-09. The label must not change with it.
        var window = new LimitWindow(TimeSpan.FromMinutes(10_079), DateTimeOffset.UnixEpoch);

        Assert.Equal("Weekly", LimitWindowClassifier.Label(window));
        Assert.Equal(LimitWindowKind.Weekly, LimitWindowClassifier.Classify(window));
    }

    [Fact]
    public void ToleranceIsAtLeastFiveMinutes()
    {
        Assert.Equal(5d, LimitWindowClassifier.ToleranceMinutes(LimitWindowClassifier.FiveHourMinutes));
        Assert.Equal(100.8d, LimitWindowClassifier.ToleranceMinutes(LimitWindowClassifier.WeeklyMinutes), 6);
    }
}
