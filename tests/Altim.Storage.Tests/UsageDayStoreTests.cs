using Altim.Core.Models;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// The day table's promises: a row round-trips its nulls as nulls, and a write merges into
/// the stored row field by field rather than replacing it.
/// </summary>
/// <remarks>
/// Two writers that never overlap share a row. The backfill owns the four token columns and
/// <c>source</c>, because only a per-day source can say what a day spent; the sample rollup
/// owns <c>peak_percent</c>, because only Altim's own readings measured the live windows. A
/// write that carries no tokens must therefore leave the tokens alone, a write that carries
/// no peak must leave the peak alone, and a peak may only ever rise. The rule this replaced —
/// "observed beats backfilled", applied to the whole row — is what let a rollup overwrite a
/// correct per-day figure with a running total, and it is retired.
/// </remarks>
public sealed class UsageDayStoreTests
{
    private static readonly DateOnly Day = new(2026, 9, 17);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A day as the backfill writes it: real per-day tokens, and no peak.</summary>
    private static UsageDay Backfilled(long input = 10, double? peak = null,
                                       DateTimeOffset? at = null) =>
        new("claude", Day, new TokenTotals(input, 1, 2, 3), peak, UsageDaySource.Backfilled,
            at ?? DateTimeOffset.UnixEpoch);

    /// <summary>A day as the rollup writes it: a peak, and no tokens whatsoever.</summary>
    private static UsageDay RolledUp(double? peak = 40, DateTimeOffset? at = null) =>
        new("claude", Day, Tokens: null, peak, UsageDaySource.Observed,
            at ?? DateTimeOffset.UnixEpoch);

    private static SqliteUsageHistoryService Service(TempDatabase temp) => new(temp.Open());

    [Fact]
    public async Task ADayRoundTripsIncludingItsNulls()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        var day = new UsageDay("claude", Day, new TokenTotals(5, null, null, null),
                               PeakPercent: null, UsageDaySource.Observed, DateTimeOffset.UnixEpoch);
        await service.UpsertDaysAsync([day], Ct);

        UsageDay stored = Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct));
        Assert.Equal(5, stored.Tokens!.Input);
        Assert.Null(stored.Tokens.Output);
        Assert.Null(stored.PeakPercent);
        Assert.Equal(UsageDaySource.Observed, stored.Source);
    }

    /// <summary>
    /// The defect this whole rule exists for. The rollup runs minutes after the backfill and
    /// offers the same day with no tokens; under the retired whole-row rule its emptiness won
    /// and the day's real figure was gone.
    /// </summary>
    [Fact]
    public async Task ARollupDoesNotEraseBackfilledTokens()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([Backfilled(input: 10)], Ct);
        await service.UpsertDaysAsync([RolledUp(peak: 40)], Ct);

        UsageDay stored = Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct));

        Assert.Equal(10, stored.Tokens!.Input);
        Assert.Equal(1, stored.Tokens.Output);
        Assert.Equal(40, stored.PeakPercent);

        // The source describes where the token figure came from, and the rollup brought none,
        // so it has no standing to relabel the row.
        Assert.Equal(UsageDaySource.Backfilled, stored.Source);
    }

    /// <summary>
    /// The same rule from the other side: the backfill knows nothing about the live windows,
    /// so a peak the rollup measured survives it.
    /// </summary>
    [Fact]
    public async Task ABackfillDoesNotEraseAPeak()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([RolledUp(peak: 40)], Ct);
        await service.UpsertDaysAsync([Backfilled(input: 10)], Ct);

        UsageDay stored = Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct));

        Assert.Equal(40, stored.PeakPercent);
        Assert.Equal(10, stored.Tokens!.Input);
        Assert.Equal(UsageDaySource.Backfilled, stored.Source);
    }

    /// <summary>
    /// A peak is the highest the day ever reached, so a later reading that found the window
    /// quieter — a fresh window, or a rollup over samples the retention pass has since
    /// thinned — describes a moment, not the day.
    /// </summary>
    [Fact]
    public async Task ARollupNeverLowersAPeak()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([RolledUp(peak: 80)], Ct);
        await service.UpsertDaysAsync([RolledUp(peak: 40)], Ct);

        Assert.Equal(80, Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct)).PeakPercent);
    }

    [Fact]
    public async Task AHigherPeakReplacesALowerOne()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([RolledUp(peak: 40)], Ct);
        await service.UpsertDaysAsync([RolledUp(peak: 80)], Ct);

        Assert.Equal(80, Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct)).PeakPercent);
    }

    /// <summary>
    /// A peak appearing where there was none is a rise, not a no-op. Comparing a null with
    /// <c>&gt;</c> alone answers neither yes nor no, which reads as no and freezes the day at
    /// unknown for good.
    /// </summary>
    [Fact]
    public async Task APeakAppearingWhereThereWasNoneIsWritten()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([Backfilled(input: 10, peak: null)], Ct);
        await service.UpsertDaysAsync([RolledUp(peak: 40)], Ct);

        Assert.Equal(40, Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct)).PeakPercent);
    }

    [Fact]
    public async Task ALaterBackfillReplacesAnEarlierOnesTokens()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([Backfilled(input: 999)], Ct);
        await service.UpsertDaysAsync([Backfilled(input: 10)], Ct);

        Assert.Equal(10, Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct)).Tokens!.Input);
    }

    /// <summary>
    /// Tokens arriving where there were none is a change even though three of the four
    /// columns were null on both sides, which is why the comparison is <c>IS NOT</c> and not
    /// <c>&lt;&gt;</c>.
    /// </summary>
    [Fact]
    public async Task TokensArrivingWhereThereWereNoneAreWritten()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([RolledUp(peak: 40)], Ct);
        await service.UpsertDaysAsync(
            [new UsageDay("claude", Day, new TokenTotals(10, null, null, null), PeakPercent: null,
                          UsageDaySource.Backfilled, DateTimeOffset.UnixEpoch)],
            Ct);

        UsageDay stored = Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct));

        Assert.Equal(10, stored.Tokens!.Input);
        Assert.Equal(40, stored.PeakPercent);
    }

    /// <summary>
    /// The same rule where nothing else moves at all. A backfill that learns one more
    /// component reports the same figure for the rest, so the only difference in the whole
    /// row is a number standing where a null was. <c>&lt;&gt;</c> answers NULL to that, which
    /// a <c>WHERE</c> reads as "no change", and the component would never be stored.
    /// </summary>
    [Fact]
    public async Task AComponentAppearingBesideUnchangedOnesIsWritten()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync(
            [new UsageDay("claude", Day, new TokenTotals(10, null, null, null), PeakPercent: null,
                          UsageDaySource.Backfilled, DateTimeOffset.UnixEpoch)],
            Ct);

        await service.UpsertDaysAsync(
            [new UsageDay("claude", Day, new TokenTotals(10, 5, null, null), PeakPercent: null,
                          UsageDaySource.Backfilled, DateTimeOffset.UnixEpoch)],
            Ct);

        UsageDay stored = Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct));

        Assert.Equal(10, stored.Tokens!.Input);
        Assert.Equal(5, stored.Tokens.Output);
    }

    /// <summary>
    /// <c>source</c> travels with the token figure, so a backfill that reports exactly the
    /// figures already stored still relabels the row: that is now where the number came from,
    /// and the tooltip reads the label out.
    /// </summary>
    [Fact]
    public async Task ABackfillReportingTheStoredFiguresStillRelabelsTheRow()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync(
            [new UsageDay("claude", Day, new TokenTotals(10, 1, 2, 3), PeakPercent: null,
                          UsageDaySource.Observed, DateTimeOffset.UnixEpoch)],
            Ct);

        await service.UpsertDaysAsync([Backfilled(input: 10)], Ct);

        Assert.Equal(UsageDaySource.Backfilled,
                     Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct)).Source);
    }

    /// <summary>
    /// The write-ahead log depends on this. A write that changes nothing must change nothing,
    /// stamp included: SQLite's own change counter is what stamps the write clock that
    /// <see cref="AltimDatabase.CheckpointIfIdleAsync"/> reads, so a row rewritten on every
    /// maintenance pass would leave the log unemptiable on a machine left switched on.
    /// </summary>
    [Fact]
    public async Task AWriteThatChangesNothingLeavesTheStampAlone()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([Backfilled(input: 10, peak: 40)], Ct);
        object? stamp = temp.Scalar("SELECT updated_at FROM usage_day");

        await service.UpsertDaysAsync(
            [Backfilled(input: 10, peak: 40, at: DateTimeOffset.UnixEpoch.AddDays(9))], Ct);

        Assert.Equal(stamp, temp.Scalar("SELECT updated_at FROM usage_day"));
    }

    /// <summary>
    /// The same guard where the write does carry something and it is simply not an
    /// improvement: a peak that would lower the day is refused, and refusing it must not
    /// leave the row looking freshly written.
    /// </summary>
    [Fact]
    public async Task AWriteThatOnlyOffersALowerPeakLeavesTheStampAlone()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([RolledUp(peak: 80)], Ct);
        object? stamp = temp.Scalar("SELECT updated_at FROM usage_day");

        await service.UpsertDaysAsync([RolledUp(peak: 40, at: DateTimeOffset.UnixEpoch.AddDays(9))], Ct);

        Assert.Equal(stamp, temp.Scalar("SELECT updated_at FROM usage_day"));
    }

    /// <summary>
    /// The other half of the guard. Without this, "leave an unchanged row alone" could be
    /// satisfied by never writing anything at all.
    /// </summary>
    [Fact]
    public async Task AWriteThatDoesChangeSomethingMovesTheStamp()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([RolledUp(peak: 40)], Ct);

        DateTimeOffset later = DateTimeOffset.UnixEpoch.AddDays(9);
        await service.UpsertDaysAsync([RolledUp(peak: 80, at: later)], Ct);

        Assert.Equal(later.ToUnixTimeSeconds(), temp.ScalarInt64("SELECT updated_at FROM usage_day"));
    }

    [Fact]
    public async Task ADayWithNoRowIsAbsentRatherThanZero()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([Backfilled()], Ct);

        IReadOnlyList<UsageDay> days =
            await service.GetDaysAsync("claude", Day.AddDays(-3), Day, Ct);

        Assert.Single(days);
        Assert.Equal(Day, days[0].Day);
    }

    [Fact]
    public async Task ClearingHistoryAlsoClearsTheDays()
    {
        using var temp = new TempDatabase();
        SqliteUsageHistoryService service = Service(temp);

        await service.UpsertDaysAsync([Backfilled()], Ct);
        await service.ClearAsync(Ct);

        Assert.Empty(await service.GetDaysAsync("claude", Day, Day, Ct));
    }
}
