using Altim.Core.Models;
using Altim.Core.Tests.Fakes;
using Altim.Core.Usage;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Reset arithmetic on a controlled clock, including what happens as the boundary is
/// crossed.
/// </summary>
public sealed class ResetCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoWindowMeansNoAnswer()
    {
        ResetCalculator calculator = Calculator();

        Assert.Null(calculator.TimeUntilReset(null));
        Assert.Null(calculator.DescribeTimeUntilReset(null));
        Assert.False(calculator.HasReset(null));
    }

    [Fact]
    public void AWindowWithoutAResetInstantIsNeverTreatedAsReset()
    {
        ResetCalculator calculator = Calculator();
        var window = new LimitWindow(TimeSpan.FromMinutes(300), ResetsAt: null);

        Assert.Null(calculator.TimeUntilReset(window));
        Assert.Null(calculator.DescribeTimeUntilReset(window));
        Assert.False(calculator.HasReset(window));
    }

    [Fact]
    public void TimeRemainingIsMeasuredFromTheInjectedClock()
    {
        ResetCalculator calculator = Calculator();
        var window = new LimitWindow(TimeSpan.FromMinutes(300), Now.AddMinutes(134));

        Assert.Equal<TimeSpan?>(TimeSpan.FromMinutes(134), calculator.TimeUntilReset(window));
        Assert.Equal("2h 14m", calculator.DescribeTimeUntilReset(window));
        Assert.False(calculator.HasReset(window));
    }

    [Fact]
    public void CrossingTheResetBoundaryFlipsTheAnswer()
    {
        var time = new TestTimeProvider(Now);
        var calculator = new ResetCalculator(time);
        var window = new LimitWindow(TimeSpan.FromMinutes(300), Now.AddMinutes(30));

        Assert.False(calculator.HasReset(window));
        Assert.Equal("30m", calculator.DescribeTimeUntilReset(window));

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.False(calculator.HasReset(window));
        Assert.Equal("1m", calculator.DescribeTimeUntilReset(window));

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(calculator.HasReset(window));
        Assert.Equal("under a minute", calculator.DescribeTimeUntilReset(window));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(calculator.HasReset(window));
        Assert.Equal<TimeSpan?>(TimeSpan.Zero, calculator.TimeUntilReset(window));

        time.Advance(TimeSpan.FromHours(3));
        Assert.True(calculator.HasReset(window));
        Assert.Equal<TimeSpan?>(TimeSpan.Zero, calculator.TimeUntilReset(window));
    }

    [Fact]
    public void RemainingTimeIsNeverNegative()
    {
        ResetCalculator calculator = Calculator();

        Assert.Equal(TimeSpan.Zero, calculator.TimeUntil(Now.AddHours(-9)));
        Assert.True(calculator.HasPassed(Now));
        Assert.False(calculator.HasPassed(Now.AddTicks(1)));
    }

    [Theory]
    [InlineData(0, "under a minute")]
    [InlineData(59, "under a minute")]
    [InlineData(60, "1m")]
    [InlineData(14 * 60, "14m")]
    [InlineData(59 * 60 + 59, "59m")]
    [InlineData(3_600, "1h")]
    [InlineData(8_070, "2h 14m")]
    [InlineData(86_400, "1d")]
    [InlineData(93_600, "1d 2h")]
    [InlineData(280_800, "3d 6h")]
    public void DurationsAreHumanisedByTruncation(int seconds, string expected) =>
        Assert.Equal(expected, ResetCalculator.Humanise(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ANegativeDurationReadsAsUnderAMinuteRatherThanGoingBackwards() =>
        Assert.Equal("under a minute", ResetCalculator.Humanise(TimeSpan.FromMinutes(-40)));

    [Fact]
    public void SecondsAreTruncatedNotRoundedUp() =>
        Assert.Equal("2h 14m", ResetCalculator.Humanise(TimeSpan.FromSeconds(8_099)));

    private static ResetCalculator Calculator() => new(new TestTimeProvider(Now));
}
