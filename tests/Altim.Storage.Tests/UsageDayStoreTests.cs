using Altim.Core.Models;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// The day table's two promises: a row round-trips its nulls as nulls, and a day Altim
/// watched itself is never rewritten by a later backfill.
/// </summary>
public sealed class UsageDayStoreTests
{
    private static readonly DateOnly Day = new(2026, 9, 17);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UsageDay Make(UsageDaySource source, long input = 10, double? peak = 40) =>
        new("claude", Day, new TokenTotals(input, 1, 2, 3), peak, source, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task ADayRoundTripsIncludingItsNulls()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var service = new SqliteUsageHistoryService(database);

        var day = new UsageDay("claude", Day, new TokenTotals(5, null, null, null),
                               PeakPercent: null, UsageDaySource.Observed, DateTimeOffset.UnixEpoch);
        await service.UpsertDaysAsync([day], Ct);

        UsageDay stored = Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct));
        Assert.Equal(5, stored.Tokens!.Input);
        Assert.Null(stored.Tokens.Output);
        Assert.Null(stored.PeakPercent);
        Assert.Equal(UsageDaySource.Observed, stored.Source);
    }

    [Fact]
    public async Task ABackfilledDayNeverReplacesAnObservedOne()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var service = new SqliteUsageHistoryService(database);

        await service.UpsertDaysAsync([Make(UsageDaySource.Observed, input: 10)], Ct);
        await service.UpsertDaysAsync([Make(UsageDaySource.Backfilled, input: 999)], Ct);

        UsageDay stored = Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct));
        Assert.Equal(10, stored.Tokens!.Input);
        Assert.Equal(UsageDaySource.Observed, stored.Source);
    }

    [Fact]
    public async Task AnObservedDayReplacesABackfilledOne()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var service = new SqliteUsageHistoryService(database);

        await service.UpsertDaysAsync([Make(UsageDaySource.Backfilled, input: 999)], Ct);
        await service.UpsertDaysAsync([Make(UsageDaySource.Observed, input: 10)], Ct);

        UsageDay stored = Assert.Single(await service.GetDaysAsync("claude", Day, Day, Ct));
        Assert.Equal(10, stored.Tokens!.Input);
    }

    [Fact]
    public async Task ADayWithNoRowIsAbsentRatherThanZero()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var service = new SqliteUsageHistoryService(database);

        await service.UpsertDaysAsync([Make(UsageDaySource.Observed)], Ct);

        IReadOnlyList<UsageDay> days =
            await service.GetDaysAsync("claude", Day.AddDays(-3), Day, Ct);

        Assert.Single(days);
        Assert.Equal(Day, days[0].Day);
    }

    [Fact]
    public async Task ClearingHistoryAlsoClearsTheDays()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var service = new SqliteUsageHistoryService(database);

        await service.UpsertDaysAsync([Make(UsageDaySource.Observed)], Ct);
        await service.ClearAsync(Ct);

        Assert.Empty(await service.GetDaysAsync("claude", Day, Day, Ct));
    }
}
