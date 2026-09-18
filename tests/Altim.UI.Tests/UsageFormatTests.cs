using Altim.Core.Models;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The formatters' contract: nothing reported comes back as nothing, never as a zero and never
/// as a guess.
/// </summary>
public sealed class UsageFormatTests
{
    /// <summary>A reported percentage reads as a whole number with a sign.</summary>
    [Fact]
    public void FormatsAPercentage() => Assert.Equal("62%", UsageFormat.Percent(62d));

    /// <summary>An unreported percentage has no text at all, rather than a zero.</summary>
    [Fact]
    public void RefusesToInventAPercentage()
    {
        Assert.Null(UsageFormat.Percent(null));
        Assert.Null(UsageFormat.Percent(double.NaN));
        Assert.Null(UsageFormat.Percent(-1d));
    }

    /// <summary>
    /// A value above the plausible ceiling is a known provider defect, not a level, so it is
    /// unreported rather than clamped to a number nobody measured.
    /// </summary>
    [Fact]
    public void RefusesAnImplausiblePercentage() =>
        Assert.Null(UsageFormat.Percent(1_760_000_000d));

    /// <summary>Counts read compactly, at the scale the number is actually at.</summary>
    /// <param name="value">The count.</param>
    /// <param name="expected">How it reads.</param>
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(842L, "842")]
    [InlineData(56_200L, "56.2K")]
    [InlineData(130_000L, "130K")]
    [InlineData(1_300_000L, "1.3M")]
    public void FormatsCounts(long value, string expected) => Assert.Equal(expected, UsageFormat.Count(value));

    /// <summary>Tokens are shown only when a provider reports them.</summary>
    [Fact]
    public void ShowsTokensOnlyWhenReported()
    {
        Assert.Null(UsageFormat.Tokens(null));
        Assert.Null(UsageFormat.Tokens(new TokenTotals(null, null, 12_000, 4_000)));
        Assert.Equal("69K tokens", UsageFormat.Tokens(new TokenTotals(56_200, 12_800, null, null)));
    }

    /// <summary>A window with no reset instant produces no reset caption.</summary>
    [Fact]
    public void RefusesToInventAResetTime()
    {
        var clock = new TestClock(Readings.Now);

        Assert.Null(UsageFormat.ResetsIn(null, clock));
        Assert.Null(UsageFormat.ResetsIn(new LimitWindow(TimeSpan.FromHours(5), null), clock));
    }

    /// <summary>A window with a reset instant reads as a duration in DESIGN.md's form.</summary>
    [Fact]
    public void FormatsTheTimeUntilReset()
    {
        var clock = new TestClock(Readings.Now);
        var window = new LimitWindow(TimeSpan.FromHours(5), Readings.Now.AddMinutes(134));

        Assert.Equal("2h 14m", UsageFormat.Remaining(window, clock));
        Assert.Equal("Resets in 2h 14m", UsageFormat.ResetsIn(window, clock));
    }

    /// <summary>A provider that has never answered says so rather than showing a time.</summary>
    [Fact]
    public void SaysWhenNothingHasBeenRead() =>
        Assert.Equal(UsageFormat.NotRefreshedYet, UsageFormat.LastRefreshed(null));

    /// <summary>
    /// Two windows of one length are not named the same thing.
    /// </summary>
    /// <remarks>
    /// The shorthand was keyed on the window's length alone, and Claude Code reports Weekly,
    /// Weekly (Opus) and Weekly (Sonnet), all seven days long. The tray panel's one line
    /// therefore printed "7d" three times with three different figures beside it: three
    /// readings under one name, on the surface with the least room to sort it out.
    /// </remarks>
    [Fact]
    public void NamesTwoWindowsOfOneLengthApart()
    {
        var week = new LimitWindow(TimeSpan.FromDays(7), null);

        Assert.Equal("7d", UsageFormat.MetricShort(week, "Weekly"));
        Assert.Equal("7d Opus", UsageFormat.MetricShort(week, "Weekly (Opus)"));
        Assert.Equal("7d Sonnet", UsageFormat.MetricShort(week, "Weekly (Sonnet)"));

        // The plain cases are untouched, and a window nobody reported still has no shorthand.
        Assert.Equal("5h", UsageFormat.MetricShort(new LimitWindow(TimeSpan.FromHours(5), null), "Session"));
        Assert.Equal("30d", UsageFormat.MetricShort(new LimitWindow(TimeSpan.FromDays(30), null), "Monthly"));
        Assert.Null(UsageFormat.MetricShort(null, "Weekly"));
        Assert.Null(UsageFormat.MetricShort(new LimitWindow(TimeSpan.Zero, null), "Weekly"));

        // A label with nothing between the brackets adds nothing rather than a trailing space.
        Assert.Equal("7d", UsageFormat.MetricShort(week, "Weekly ()"));
        Assert.Equal("7d", UsageFormat.MetricShort(week, "Weekly ("));
        Assert.Equal("7d", UsageFormat.MetricShort(week, null));
    }

    /// <summary>The unavailable sentences are the ones DESIGN.md prescribes, exactly.</summary>
    [Fact]
    public void KeepsTheWording()
    {
        Assert.Equal("Not reported by this provider", UsageFormat.MetricUnavailable);
        Assert.Equal("Waiting for the first reading.", UsageFormat.WaitingForFirstReading);
        Assert.Equal("Unable to retrieve usage", UsageFormat.ProviderUnavailable);

        // The metric block and the integration section say the fourth state in one voice,
        // which is only true while they read the same constant.
        Assert.Equal(
            UsageFormat.WaitingForFirstReading,
            UsageFormat.IntegrationSentence(ProviderStatus.Unknown));
        Assert.Equal("Retry", UsageFormat.RetryLabel);
        Assert.Equal(
            "No usage recorded yet. Altim starts collecting when an agent runs.",
            UsageFormat.HistoryEmpty);
    }
}
