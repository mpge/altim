using Altim.Core.Models;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// What one provider used on one day, and the one rule that matters about it: an
/// unreported figure is null, never zero, all the way through the total.
/// </summary>
public sealed class UsageDayTests
{
    [Fact]
    public void TotalTokensSumsTheComponentsThatWereReported()
    {
        var day = new UsageDay(
            "claude",
            new DateOnly(2026, 9, 17),
            new TokenTotals(Input: 10, Output: 5, CacheRead: 100, CacheWrite: null),
            PeakPercent: 42,
            UsageDaySource.Observed,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(115, day.TotalTokens);
    }

    [Fact]
    public void ADayThatReportedNothingHasNoTotalRatherThanZero()
    {
        var day = new UsageDay(
            "claude", new DateOnly(2026, 9, 17), Tokens: null,
            PeakPercent: null, UsageDaySource.Backfilled, DateTimeOffset.UnixEpoch);

        Assert.Null(day.TotalTokens);
    }
}
