using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The greeting's contract: three forms, computed from the local hour, never stored and never
/// assumed. The cutovers are tested at the exact hour they change on, because an off-by-one
/// here is a greeting that reads as wrong to anyone awake at that hour.
/// </summary>
public sealed class GreetingTests
{
    /// <summary>Each hour of the day reads as the form DESIGN.md allows.</summary>
    /// <param name="hour">The local hour.</param>
    /// <param name="expected">The greeting it reads as.</param>
    [Theory]
    [InlineData(0, Greeting.Evening)]
    [InlineData(4, Greeting.Evening)]
    [InlineData(5, Greeting.Morning)]
    [InlineData(6, Greeting.Morning)]
    [InlineData(11, Greeting.Morning)]
    [InlineData(12, Greeting.Afternoon)]
    [InlineData(13, Greeting.Afternoon)]
    [InlineData(17, Greeting.Afternoon)]
    [InlineData(18, Greeting.Evening)]
    [InlineData(23, Greeting.Evening)]
    public void ReadsTheHour(int hour, string expected) => Assert.Equal(expected, Greeting.ForHour(hour));

    /// <summary>Morning starts at its stated hour, and the hour before it is still evening.</summary>
    [Fact]
    public void MorningStartsOnTheCutover()
    {
        Assert.Equal(Greeting.Evening, Greeting.ForHour(Greeting.MorningStartHour - 1));
        Assert.Equal(Greeting.Morning, Greeting.ForHour(Greeting.MorningStartHour));
    }

    /// <summary>Afternoon starts at noon, and the hour before it is still morning.</summary>
    [Fact]
    public void AfternoonStartsOnTheCutover()
    {
        Assert.Equal(Greeting.Morning, Greeting.ForHour(Greeting.AfternoonStartHour - 1));
        Assert.Equal(Greeting.Afternoon, Greeting.ForHour(Greeting.AfternoonStartHour));
    }

    /// <summary>Evening starts at its stated hour, and the hour before it is still afternoon.</summary>
    [Fact]
    public void EveningStartsOnTheCutover()
    {
        Assert.Equal(Greeting.Afternoon, Greeting.ForHour(Greeting.EveningStartHour - 1));
        Assert.Equal(Greeting.Evening, Greeting.ForHour(Greeting.EveningStartHour));
    }

    /// <summary>The greeting comes from the clock rather than from anything stored.</summary>
    [Fact]
    public void ReadsTheClock()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 15, 8, 30, 0, TimeSpan.Zero));
        Assert.Equal(Greeting.Morning, Greeting.Now(clock));

        clock.UtcNow = clock.UtcNow.AddHours(5);
        Assert.Equal(Greeting.Afternoon, Greeting.Now(clock));

        clock.UtcNow = clock.UtcNow.AddHours(7);
        Assert.Equal(Greeting.Evening, Greeting.Now(clock));
    }

    /// <summary>An hour that is not an hour is a mistake, not a default.</summary>
    [Fact]
    public void RefusesAnHourThatIsNotAnHour()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Greeting.ForHour(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Greeting.ForHour(24));
    }

    /// <summary>The line under the greeting is the one DESIGN.md prescribes.</summary>
    [Fact]
    public void KeepsTheSubHeading() =>
        Assert.Equal("Here's how your AI agents are doing.", Greeting.SubHeading);
}
