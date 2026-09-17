using Altim.Core.Models;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// The rollup against a real file: Altim's own samples become one observed row per
/// provider per <em>local</em> day, recomputing a day rewrites it rather than duplicating
/// it, a day that reported nothing stays unknown, and a range with nothing to write takes
/// no writer at all.
/// </summary>
public sealed class UsageDayRollupIntegrationTests
{
    private static readonly DateOnly First = new(2026, 9, 16);
    private static readonly DateOnly Second = new(2026, 9, 17);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SamplesAcrossTwoDaysBecomeOneRowEachTakingTheDaysHighestReading()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await Record(history, First, hour: 9, input: 100, percent: 10);
        await Record(history, First, hour: 18, input: 250, percent: 30);

        // The second day's counter falls back within the day, which the Codex reader can
        // legitimately do: the day is still worth its highest reading, not its last.
        await Record(history, Second, hour: 9, input: 900, percent: 80);
        await Record(history, Second, hour: 18, input: 400, percent: 20);

        int written = await history.RollUpDaysAsync(First, Second, Ct);

        Assert.Equal(2, written);

        IReadOnlyList<UsageDay> days = await history.GetDaysAsync("claude", First, Second, Ct);

        Assert.Equal(2, days.Count);
        Assert.Equal(First, days[0].Day);
        Assert.Equal(250, days[0].Tokens!.Input);
        Assert.Equal(30, days[0].PeakPercent);
        Assert.Equal(Second, days[1].Day);
        Assert.Equal(900, days[1].Tokens!.Input);
        Assert.Equal(80, days[1].PeakPercent);
        Assert.All(days, day => Assert.Equal(UsageDaySource.Observed, day.Source));
    }

    [Fact]
    public async Task RollingUpTwiceProducesTheSameRows()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await SeedTwoDaysAsync(history);

        int first = await history.RollUpDaysAsync(First, Second, Ct);
        IReadOnlyList<UsageDay> after = await history.GetDaysAsync("claude", First, Second, Ct);

        int second = await history.RollUpDaysAsync(First, Second, Ct);
        IReadOnlyList<UsageDay> again = await history.GetDaysAsync("claude", First, Second, Ct);

        Assert.Equal(first, second);

        // Everything but the stamp: the row is recomputed, so when it was last written
        // moves and nothing else may.
        Assert.Equal(after.Select(day => day with { UpdatedAt = DateTimeOffset.UnixEpoch }),
                     again.Select(day => day with { UpdatedAt = DateTimeOffset.UnixEpoch }));
    }

    /// <summary>
    /// The range routinely includes today, which is a day still being lived: rolling it up
    /// again has to update the one row rather than add a second, and may only raise it.
    /// </summary>
    [Fact]
    public async Task ADayStillInProgressIsRewrittenRatherThanDuplicated()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await Record(history, Second, hour: 9, input: 100, percent: 10);
        _ = await history.RollUpDaysAsync(Second, Second, Ct);

        // The day carries on.
        await Record(history, Second, hour: 18, input: 400, percent: 55);
        int written = await history.RollUpDaysAsync(Second, Second, Ct);

        Assert.Equal(1, written);
        Assert.Equal(1L, temp.CountRows("usage_day"));

        UsageDay day = Assert.Single(await history.GetDaysAsync("claude", Second, Second, Ct));
        Assert.Equal(400, day.Tokens!.Input);
        Assert.Equal(55, day.PeakPercent);
        Assert.Equal(UsageDaySource.Observed, day.Source);
    }

    /// <summary>
    /// A reading that failed is not stored at all, by design, so the day it failed on has
    /// nothing to roll up and stays unknown rather than being written as zero.
    /// </summary>
    [Fact]
    public async Task ADayWhoseOnlyReadingErroredProducesNoRow()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        var errored = new ProviderUsage("claude", ProviderStatus.Error,
                                        [Metric(percent: 40)], new TokenTotals(100, null, null, null),
                                        LocalAt(Second, hour: 9), null);
        await history.RecordAsync(errored, Ct);

        Assert.Equal(0L, temp.CountRows("usage_sample"));

        int written = await history.RollUpDaysAsync(Second, Second, Ct);

        Assert.Equal(0, written);
        Assert.Equal(0L, temp.CountRows("usage_day"));
    }

    /// <summary>
    /// A stored reading that reported neither tokens nor a usable percentage rolls up to
    /// nothing. It must not be counted as written, and must not leave an empty observed row
    /// that would read as unknown on the map while outranking a backfill that did know.
    /// </summary>
    [Fact]
    public async Task ADayThatReportedNothingIsNeitherWrittenNorCounted()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(
            new ProviderUsage("claude", ProviderStatus.Idle, [Metric(percent: null)], null,
                              LocalAt(Second, hour: 9), null), Ct);

        Assert.Equal(1L, temp.CountRows("usage_sample"));

        int written = await history.RollUpDaysAsync(Second, Second, Ct);

        Assert.Equal(0, written);
        Assert.Equal(0L, temp.CountRows("usage_day"));
        Assert.Empty(await history.GetDaysAsync("claude", Second, Second, Ct));
    }

    /// <summary>
    /// Grouping is by provider <em>and</em> day. Nothing else in the plan's tests would
    /// notice a rollup that merged two providers into one row, and merging them would also
    /// combine two percentages measured against different limits.
    /// </summary>
    [Fact]
    public async Task TwoProvidersOnTheSameDayStayTwoRows()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await Record(history, Second, hour: 9, input: 100, percent: 10);
        await Record(history, Second, hour: 18, input: 250, percent: 30);
        await Record(history, Second, hour: 10, input: 7_000, percent: 90, providerId: "codex");
        await Record(history, Second, hour: 20, input: 9_000, percent: 95, providerId: "codex");

        int written = await history.RollUpDaysAsync(Second, Second, Ct);

        Assert.Equal(2, written);

        UsageDay claude = Assert.Single(await history.GetDaysAsync("claude", Second, Second, Ct));
        UsageDay codex = Assert.Single(await history.GetDaysAsync("codex", Second, Second, Ct));

        Assert.Equal(250, claude.Tokens!.Input);
        Assert.Equal(30, claude.PeakPercent);
        Assert.Equal(9_000, codex.Tokens!.Input);
        Assert.Equal(95, codex.PeakPercent);
    }

    /// <summary>
    /// A range whose end is before its start names no days, which is nothing to do rather
    /// than something to complain about — the same answer <see cref="SqliteUsageHistoryService.GetDaysAsync"/>
    /// gives for the same bounds.
    /// </summary>
    [Fact]
    public async Task AnInvertedRangeRollsUpNothing()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await SeedTwoDaysAsync(history);

        int written = await history.RollUpDaysAsync(Second, First, Ct);

        Assert.Equal(0, written);
        Assert.Equal(0L, temp.CountRows("usage_day"));
    }

    /// <summary>
    /// Taking the writer stamps the write clock that <see cref="AltimDatabase.CheckpointIfIdleAsync"/>
    /// watches, so a rollup that found nothing must not ask for it: a rollup on every
    /// maintenance pass would otherwise keep the WAL from ever being truncated.
    /// </summary>
    /// <remarks>
    /// Asserted by holding the writer from here. The empty range has to finish anyway; the
    /// range with samples in it cannot, which is what makes the first assertion mean
    /// something.
    /// </remarks>
    [Fact]
    public async Task ARangeWithNothingToWriteDoesNotTakeTheWriter()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var history = new SqliteUsageHistoryService(database);

        await SeedTwoDaysAsync(history);

        Task<int> occupied;
        using (WriteLease held = await database.LeaseWriterAsync(Ct))
        {
            // A real range, read properly, that simply has no samples in it.
            Task<int> empty = history
                .RollUpDaysAsync(First.AddDays(-30), First.AddDays(-20), Ct)
                .AsTask();

            // Throws a TimeoutException if it did not finish, which is the failure this
            // test is for: a rollup with nothing to write waiting for a writer it has no
            // use for. The budget is a deadlock guard and not part of the assertion — the
            // writer is held for this whole scope, so finishing at all is the proof — and
            // it is deliberately generous, because a tight one turns a loaded build agent
            // into a red suite without ever telling anyone anything true.
            Assert.Equal(0, await empty.WaitAsync(TimeSpan.FromSeconds(30), Ct));

            // The control: a range that does produce rows needs the writer, and waits.
            occupied = history.RollUpDaysAsync(First, Second, Ct).AsTask();

            _ = await Assert.ThrowsAsync<TimeoutException>(
                async () => await occupied.WaitAsync(TimeSpan.FromMilliseconds(300), Ct));
        }

        Assert.Equal(2, await occupied);
    }

    /// <summary>
    /// The day is the user's local calendar day, decided in C# because SQLite has no
    /// timezone database. Grouping on the UTC date instead would fail every assertion here.
    /// </summary>
    [Fact]
    public async Task TheDayIsTheUsersLocalCalendarDayNotTheUtcOne()
    {
        using var temp = new TempDatabase();
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "altim-test-plus-ten", TimeSpan.FromHours(10), "Altim Test +10", "Altim Test +10");
        var history = new SqliteUsageHistoryService(temp.Open(), new FixedZoneClock(zone));

        // 16th in UTC, 17th at 06:00 where the user is.
        await RecordAt(history, new DateTimeOffset(2026, 9, 16, 20, 0, 0, TimeSpan.Zero), input: 100);

        // 17th in UTC, and the last minute of the 17th where the user is.
        await RecordAt(history, new DateTimeOffset(2026, 9, 17, 13, 59, 0, TimeSpan.Zero), input: 250);

        // Still the 17th in UTC, but already the 18th where the user is, so outside the
        // range being rolled up and in nobody's row.
        await RecordAt(history, new DateTimeOffset(2026, 9, 17, 14, 0, 0, TimeSpan.Zero), input: 9_999);

        int written = await history.RollUpDaysAsync(First, Second, Ct);

        Assert.Equal(1, written);

        UsageDay day = Assert.Single(await history.GetDaysAsync("claude", First, Second, Ct));
        Assert.Equal(Second, day.Day);
        Assert.Equal(250, day.Tokens!.Input);
    }

    /// <summary>
    /// The rollup is observed, and observed outranks backfilled, so a day a backfill guessed
    /// at is put right the moment Altim's own samples can speak for it.
    /// </summary>
    [Fact]
    public async Task TheRollupReplacesABackfilledDay()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.UpsertDaysAsync(
            [new UsageDay("claude", Second, new TokenTotals(999_999, null, null, null),
                          PeakPercent: null, UsageDaySource.Backfilled, DateTimeOffset.UnixEpoch)],
            Ct);

        await Record(history, Second, hour: 9, input: 100, percent: 10);
        await Record(history, Second, hour: 18, input: 250, percent: 30);

        int written = await history.RollUpDaysAsync(Second, Second, Ct);

        Assert.Equal(1, written);
        Assert.Equal(1L, temp.CountRows("usage_day"));

        UsageDay day = Assert.Single(await history.GetDaysAsync("claude", Second, Second, Ct));
        Assert.Equal(250, day.Tokens!.Input);
        Assert.Equal(UsageDaySource.Observed, day.Source);
    }

    private static async Task SeedTwoDaysAsync(SqliteUsageHistoryService history)
    {
        await Record(history, First, hour: 9, input: 100, percent: 10);
        await Record(history, First, hour: 18, input: 250, percent: 30);
        await Record(history, Second, hour: 9, input: 400, percent: 40);
        await Record(history, Second, hour: 18, input: 900, percent: 80);
    }

    private static ValueTask Record(SqliteUsageHistoryService history, DateOnly day, int hour,
                                    long input, double? percent, string providerId = "claude")
        => history.RecordAsync(
            new ProviderUsage(providerId, ProviderStatus.Idle, [Metric(percent)],
                              new TokenTotals(input, null, null, null), LocalAt(day, hour), null),
            Ct);

    private static ValueTask RecordAt(SqliteUsageHistoryService history, DateTimeOffset at, long input)
        => history.RecordAsync(
            new ProviderUsage("claude", ProviderStatus.Idle, [Metric(percent: null)],
                              new TokenTotals(input, null, null, null), at, null),
            Ct);

    private static UsageMetric Metric(double? percent)
        => new("five_hour", "Session", percent, null, MetricConfidence.Documented);

    /// <summary>
    /// A wall-clock instant on the machine running the test, so what local day it falls on
    /// is the same answer in every timezone. Mid-morning and evening, well away from any
    /// hour a DST jump moves.
    /// </summary>
    private static DateTimeOffset LocalAt(DateOnly day, int hour)
        => new(day.ToDateTime(new TimeOnly(hour, 0)));

    /// <summary>A clock whose local zone is fixed, so the day a sample lands on is not the agent's.</summary>
    private sealed class FixedZoneClock(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = zone;
    }
}
