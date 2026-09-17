using Altim.Core.Models;
using Altim.Core.Usage;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Rolling a day up out of the readings Altim took. The rule that costs money when it is got
/// wrong: a reading's token totals are cumulative, so the day is the highest reading of the
/// day, never the sum of them — and never the last one either, because the counter can fall
/// within a day when an old session drops out of a provider's bounded window.
/// </summary>
public sealed class UsageDayRollupTests
{
    private static readonly DateOnly Day = new(2026, 9, 17);

    private static UsageSample Sample(int hour, long? tokens, double? percent, string key = "five_hour") =>
        new("claude", key, new DateTimeOffset(2026, 9, 17, hour, 0, 0, TimeSpan.Zero),
            percent, null, null, tokens is { } t ? new TokenTotals(t, null, null, null) : null);

    private static UsageSample Totals(int hour, TokenTotals? totals, string key = "five_hour") =>
        new("claude", key, new DateTimeOffset(2026, 9, 17, hour, 0, 0, TimeSpan.Zero),
            null, null, null, totals);

    [Fact]
    public void TokensComeFromTheHighestReadingBecauseTheyAreCumulative()
    {
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 100, 10), Sample(12, 250, 20), Sample(18, 400, 30)],
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal(400, day.Tokens!.Input);
    }

    [Fact]
    public void ACounterThatFallsWithinTheDayStillReportsTheDaysHighest()
    {
        // A later reading can legitimately report less: the provider's figure is summed over
        // a bounded window of its most recent sessions, so an old session dropping out of
        // that window makes the number go backwards. Taking the last reading would report
        // 100 and silently understate a day Altim watched reach 400.
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 400, 10), Sample(12, 250, 20), Sample(18, 100, 30)],
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal(400, day.Tokens!.Input);
    }

    [Fact]
    public void TheDayIsTheHighestReadingAndNeverTheSumOfThem()
    {
        // Three readings of a cumulative counter. The day used 30 tokens; summing the
        // readings would report 60 and would grow again with every extra poll.
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 10, null), Sample(12, 20, null), Sample(18, 30, null)],
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal(30, day.Tokens!.Input);
        Assert.Equal(30, day.TotalTokens);
    }

    [Fact]
    public void AReadingThatReportedNoFiguresCannotLowerTheDay()
    {
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 400, 10), Totals(18, new TokenTotals(null, null, null, null))],
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal(400, day.Tokens!.Input);
    }

    [Fact]
    public void EachComponentTakesItsOwnHighestReading()
    {
        // The components are separate counters and are kept separately: the row is a
        // composite of the most of each that Altim saw, not a snapshot of one instant.
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Totals(9, new TokenTotals(400, 10, null, null)),
             Totals(18, new TokenTotals(100, 50, null, null))],
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal(400, day.Tokens!.Input);
        Assert.Equal(50, day.Tokens.Output);
        Assert.Null(day.Tokens.CacheRead);
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
    public void ADayWithNoPercentagesHasNoPeakRatherThanZero()
    {
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 100, null)], DateTimeOffset.UnixEpoch)!;

        Assert.Null(day.PeakPercent);
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
}
