using Altim.Core.Monitoring;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The maintenance pass's history policy, as pure arithmetic: when a provider's backfill is
/// due, where its stamp lives, and how far back a rollup reaches.
/// </summary>
/// <remarks>
/// Every one of these runs without a provider, a database or a clock, which is the whole
/// point of the policy being a function rather than a method on the runtime: the awkward
/// cases — a source that is not there, a machine resumed after a week, a clock moved
/// backwards — are the ones hardest to stage against the real thing.
/// </remarks>
public sealed class HistoryBackfillPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 11, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 17);

    [Fact]
    public void ABackfillThatHasNeverRunRunsNow() =>
        Assert.True(HistoryBackfill.ShouldRun(Now, lastRun: null, sourceAvailable: true));

    [Fact]
    public void ABackfillThatRanThisMorningDoesNotRunAgain() =>
        Assert.False(HistoryBackfill.ShouldRun(Now, Now.AddHours(-3), sourceAvailable: true));

    [Fact]
    public void ABackfillThatRanJustUnderADayAgoDoesNotRunAgain() =>
        Assert.False(HistoryBackfill.ShouldRun(Now, Now.AddDays(-1).AddSeconds(1),
                                               sourceAvailable: true));

    [Fact]
    public void ABackfillThatRanExactlyADayAgoRunsAgain() =>
        Assert.True(HistoryBackfill.ShouldRun(Now, Now - HistoryBackfill.MinimumInterval,
                                              sourceAvailable: true));

    [Fact]
    public void ABackfillThatRanAWeekAgoRunsAgain() =>
        Assert.True(HistoryBackfill.ShouldRun(Now, Now.AddDays(-7), sourceAvailable: true));

    [Fact]
    public void AtMostDailyMeansAtMostDaily() =>
        Assert.Equal(TimeSpan.FromDays(1), HistoryBackfill.MinimumInterval);

    [Fact]
    public void AProviderWithNoHistorySourceIsNeverBackfilled()
    {
        // Not "not yet": there is nothing to ask, so the days it cannot account for stay
        // unknown rather than becoming a row of zeroes.
        Assert.False(HistoryBackfill.ShouldRun(Now, lastRun: null, sourceAvailable: false));
        Assert.False(HistoryBackfill.ShouldRun(Now, Now.AddDays(-30), sourceAvailable: false));
    }

    [Fact]
    public void AStampFromTheFutureRunsRatherThanWaitingForTheClockToCatchUp()
    {
        // A clock moved backwards — a laptop that corrected itself, a restored image, a
        // deliberate change — leaves a stamp ahead of now. Subtracting would give a
        // negative age, which is never a day, and the backfill would then be "not due"
        // until real time caught up: a fortnight of unknown squares nobody could explain.
        // Running instead re-stamps it at now and heals the file on the spot.
        Assert.True(HistoryBackfill.ShouldRun(Now, Now.AddDays(14), sourceAvailable: true));
        Assert.True(HistoryBackfill.ShouldRun(Now, Now.AddSeconds(1), sourceAvailable: true));
    }

    [Fact]
    public void TheLastRunStampIsPerProvider()
    {
        Assert.NotEqual(HistoryBackfill.LastRunKey("codex"), HistoryBackfill.LastRunKey("claude"));
        Assert.Equal(HistoryBackfill.LastRunKey("claude"), HistoryBackfill.LastRunKey("claude"));
    }

    [Fact]
    public void TheStampKeyCarriesTheProviderIdAndNothingElse()
    {
        // The provider id is the only identifier that reaches storage anywhere else in
        // Altim, and it is a constant like "claude". Nothing about the machine, the account
        // or a path is allowed into a settings key.
        Assert.Equal("maintenance.last_backfill.claude", HistoryBackfill.LastRunKey("claude"));
    }

    [Fact]
    public void AStampRoundTripsThroughTheSettingTable()
    {
        string stored = HistoryBackfill.FormatLastRun(Now);

        Assert.Equal(Now, HistoryBackfill.ParseLastRun(stored));
    }

    [Fact]
    public void AStampIsWrittenAsDigitsSoEveryCultureReadsItBack()
    {
        // The setting table is a file format, not a display surface: a stamp formatted
        // under a comma-decimal culture would read back as nothing and re-run the backfill
        // on every pass forever.
        Assert.Matches("^-?[0-9]+$", HistoryBackfill.FormatLastRun(Now));
    }

    [Fact]
    public void AMissingOrUnreadableStampMeansNeverRun()
    {
        Assert.Null(HistoryBackfill.ParseLastRun(null));
        Assert.Null(HistoryBackfill.ParseLastRun(string.Empty));
        Assert.Null(HistoryBackfill.ParseLastRun("whenever"));
        Assert.Null(HistoryBackfill.ParseLastRun("2026-09-17"));
    }

    [Fact]
    public void AnUnreadableStampRunsTheBackfillRatherThanSkippingIt() =>
        Assert.True(HistoryBackfill.ShouldRun(Now, HistoryBackfill.ParseLastRun("nonsense"),
                                              sourceAvailable: true));

    [Fact]
    public void ASourceIsNeverAskedForMoreHistoryThanTheMapDraws()
    {
        // The map draws a year. Asking for more would be work nothing renders, and both
        // sources are bounded well inside it anyway.
        Assert.Equal(Today.AddDays(-365), HistoryBackfill.EarliestDay(Today));
    }

    [Fact]
    public void TheFirstPassOfAProcessReachesBackAcrossTheFullResolutionWindow()
    {
        // Nothing has been rolled up by *this* process, and the samples on disk may predate
        // the build that grew a usage_day table at all. One wide pass at start-up turns the
        // history Altim already watched into observed days rather than leaving them unknown
        // and waiting for a backfill to guess at them.
        Assert.Equal(Today.AddDays(-HistoryBackfill.MaximumRollUpDays),
                     HistoryBackfill.RollUpFrom(Today, lastRolledUp: null));
    }

    [Fact]
    public void AnOrdinaryPassRollsUpYesterdayAndToday()
    {
        // Yesterday as well as today, every pass, all day: a machine left running across
        // midnight gets yesterday's final rollup within one interval of midnight instead of
        // keeping whatever partial figure the 23:55 pass happened to see.
        Assert.Equal(Today.AddDays(-1), HistoryBackfill.RollUpFrom(Today, Today));
        Assert.Equal(Today.AddDays(-1), HistoryBackfill.RollUpFrom(Today, Today.AddDays(-1)));
    }

    [Fact]
    public void AMachineResumedAfterSeveralDaysRollsUpEveryDaySinceTheLastPass()
    {
        // Asleep from the 13th to the 17th. The pass that ran on the 13th saw that day only
        // as far as it had got, so the range reaches back to it rather than to yesterday,
        // which would leave the 13th frozen at its lunchtime figure forever.
        Assert.Equal(Today.AddDays(-4), HistoryBackfill.RollUpFrom(Today, Today.AddDays(-4)));
    }

    [Fact]
    public void AResumeAfterMonthsIsStillBounded()
    {
        // A hibernated machine, or one whose clock moved forward. The catch-up reads every
        // sample in the range, so it has a floor: beyond it the samples are hourly
        // aggregates and both providers' own history covers the ground.
        Assert.Equal(Today.AddDays(-HistoryBackfill.MaximumRollUpDays),
                     HistoryBackfill.RollUpFrom(Today, Today.AddDays(-400)));
    }

    [Fact]
    public void AClockMovedBackwardsStillRollsUpYesterdayAndToday()
    {
        // A day rolled up "in the future" must not become the start of the range, or the
        // range would be empty and today would stop being rolled up at all.
        Assert.Equal(Today.AddDays(-1), HistoryBackfill.RollUpFrom(Today, Today.AddDays(3)));
    }

    [Fact]
    public void TheRollUpRangeIsNeverEmptyAndNeverUnbounded()
    {
        foreach (int offset in new[] { -400, -36, -35, -34, -2, -1, 0, 1, 400 })
        {
            DateOnly from = HistoryBackfill.RollUpFrom(Today, Today.AddDays(offset));

            Assert.True(from <= Today.AddDays(-1),
                        "A pass must always cover yesterday as well as today.");
            Assert.True(from >= Today.AddDays(-HistoryBackfill.MaximumRollUpDays),
                        "A pass must never read further back than the bound.");
        }
    }
}
