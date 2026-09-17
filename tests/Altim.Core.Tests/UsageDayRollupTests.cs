using Altim.Core.Models;
using Altim.Core.Usage;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Rolling a day up out of the readings Altim took. The rule that cost a database full of
/// wrong rows when it was got wrong: a reading's token totals are a running total, never a
/// per-day amount, so the rollup keeps no token figure at all and speaks only for the day's
/// peak percentage.
/// </summary>
public sealed class UsageDayRollupTests
{
    private static readonly DateOnly Day = new(2026, 9, 17);

    private static UsageSample Sample(int hour, long? tokens, double? percent, string key = "five_hour") =>
        new("claude", key, new DateTimeOffset(2026, 9, 17, hour, 0, 0, TimeSpan.Zero),
            percent, null, null, tokens is { } t ? new TokenTotals(t, null, null, null) : null);

    private static UsageSample Totals(int hour, TokenTotals? totals, double? percent = 10d,
                                      string key = "five_hour") =>
        new("claude", key, new DateTimeOffset(2026, 9, 17, hour, 0, 0, TimeSpan.Zero),
            percent, null, null, totals);

    [Fact]
    public void ADayCarriesNoTokenFigureHoweverMuchItsReadingsReported()
    {
        // Every reading here reports tokens, and the day still has none: the readings are
        // running totals that happened to be seen today, and no arithmetic over them
        // produces the tokens spent today. Only a per-day source can say that.
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 100, 10), Sample(12, 250, 20), Sample(18, 400, 30)],
            DateTimeOffset.UnixEpoch)!;

        Assert.Null(day.Tokens);
        Assert.Null(day.TotalTokens);
    }

    [Fact]
    public void EveryComponentIsDroppedAndNotJustTheOnesThatMoved()
    {
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Totals(9, new TokenTotals(400, 10, 7, 3)), Totals(18, new TokenTotals(100, 50, 9, 1))],
            DateTimeOffset.UnixEpoch)!;

        Assert.Null(day.Tokens);
    }

    [Fact]
    public void ThePeakIsTheHighestPercentageAnyWindowReached()
    {
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 100, 10), Sample(12, 250, 80, "seven_day"), Sample(18, 400, 30)],
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal(80, day.PeakPercent);
    }

    [Fact]
    public void ThePeakIsNormalisedRatherThanTakenAsReported()
    {
        // The known defect where a Unix timestamp lands in the percentage field. A reading
        // Altim would refuse to show is not a reading it may take a peak from, so the day
        // is worth the 30 it really saw and not the nonsense.
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, null, 30), Sample(18, null, 1_763_000_000d)],
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal(30, day.PeakPercent);
    }

    [Fact]
    public void ADayThatReportedNoPercentageAtAllRollsUpToNothing()
    {
        // Tokens in every reading, and still nothing to say: the tokens are not this day's,
        // and with no percentage there is no fact about the day left to write down. A row
        // here would claim Altim knew something about the day that it does not.
        Assert.Null(UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 100, null), Sample(18, 400, null)], DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ADayWithNoSamplesRollsUpToNothingAtAll()
    {
        Assert.Null(UsageDayRollup.FromSamples("claude", Day, [], DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void TheResultIsAlwaysObservedAndAlwaysTheSameForTheSameInput()
    {
        UsageSample[] samples = [Sample(9, 100, 10), Sample(18, 400, 30)];

        UsageDay first = UsageDayRollup.FromSamples("claude", Day, samples, DateTimeOffset.UnixEpoch)!;
        UsageDay again = UsageDayRollup.FromSamples("claude", Day, samples, DateTimeOffset.UnixEpoch)!;

        Assert.Equal(UsageDaySource.Observed, first.Source);
        Assert.Equal(first, again);
    }

    [Fact]
    public void TheOrderTheReadingsArriveInCannotChangeTheDay()
    {
        UsageDay forwards = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 100, 10), Sample(12, 250, 80), Sample(18, 400, 30)],
            DateTimeOffset.UnixEpoch)!;

        UsageDay backwards = UsageDayRollup.FromSamples("claude", Day,
            [Sample(18, 400, 30), Sample(12, 250, 80), Sample(9, 100, 10)],
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal(forwards, backwards);
    }
}
