using Altim.Core.Models;
using Altim.Core.Usage;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Rolling a day up out of the readings Altim took. The rule that costs money when it is
/// got wrong: a reading's token totals are cumulative, so the day is the last reading that
/// reported any, never the sum of them.
/// </summary>
public sealed class UsageDayRollupTests
{
    private static readonly DateOnly Day = new(2026, 9, 17);

    private static UsageSample Sample(int hour, long? tokens, double? percent, string key = "five_hour") =>
        new("claude", key, new DateTimeOffset(2026, 9, 17, hour, 0, 0, TimeSpan.Zero),
            percent, null, null, tokens is { } t ? new TokenTotals(t, null, null, null) : null);

    [Fact]
    public void TokensComeFromTheLastReadingBecauseTheyAreCumulative()
    {
        UsageDay day = UsageDayRollup.FromSamples("claude", Day,
            [Sample(9, 100, 10), Sample(12, 250, 20), Sample(18, 400, 30)],
            DateTimeOffset.UnixEpoch)!;

        Assert.Equal(400, day.Tokens!.Input);
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
