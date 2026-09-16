using Altim.Core.Notifications;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// The state that stops a notification firing twice. It has to round trip exactly,
/// including the reset instant that may not exist, and it has to be clearable.
/// </summary>
public sealed class NotificationStateStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFiringRoundTrips()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        var state = new NotificationState("claude", "five_hour", 80, Now, Now.AddHours(3));
        await store.RecordAsync(state, Ct);

        Assert.Equal(state, Assert.Single(await store.GetAllAsync(Ct)));
    }

    [Fact]
    public async Task AFiringWithNoKnownResetKeepsItsNull()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        await store.RecordAsync(new NotificationState("codex", "codex:10080", 95, Now, null), Ct);

        NotificationState stored = Assert.Single(await store.GetAllAsync(Ct));

        Assert.Null(stored.WindowResetsAt);
        Assert.Equal(DBNull.Value, temp.Scalar("SELECT window_resets_at FROM notification_state"));
    }

    [Fact]
    public async Task RecordingTheSameThresholdAgainUpdatesInPlace()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        await store.RecordAsync(new NotificationState("claude", "five_hour", 80, Now, Now.AddHours(3)),
                                Ct);
        await store.RecordAsync(
            new NotificationState("claude", "five_hour", 80, Now.AddHours(5), Now.AddHours(8)), Ct);

        NotificationState stored = Assert.Single(await store.GetAllAsync(Ct));

        Assert.Equal(1L, temp.CountRows("notification_state"));
        Assert.Equal(Now.AddHours(5), stored.FiredAt);
        Assert.Equal(Now.AddHours(8), stored.WindowResetsAt);
    }

    [Fact]
    public async Task ThresholdsAreReadBackPerMetric()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        await Seed(store);

        IReadOnlyList<NotificationState> sessionState = await store.GetAsync("claude", "five_hour", Ct);

        Assert.Equal(new[] { 80, 95 }, sessionState.Select(s => s.Threshold).ToArray());
        Assert.Equal(4, (await store.GetAllAsync(Ct)).Count);
    }

    [Fact]
    public async Task ClearingOneMetricLeavesTheOthers()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        await Seed(store);

        Assert.Equal(2, await store.ClearMetricAsync("claude", "five_hour", Ct));

        IReadOnlyList<NotificationState> left = await store.GetAllAsync(Ct);

        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, s => s.MetricKey == "five_hour");
    }

    [Fact]
    public async Task OnlyWindowsThatHavePassedAreForgotten()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        await store.RecordAsync(new NotificationState("claude", "five_hour", 80, Now, Now.AddHours(1)),
                                Ct);
        await store.RecordAsync(new NotificationState("claude", "seven_day", 80, Now, Now.AddDays(3)),
                                Ct);

        // No reported reset instant, so there is no moment Altim can say has passed.
        await store.RecordAsync(new NotificationState("codex", "codex:10080", 80, Now, null), Ct);

        Assert.Equal(1, await store.ClearExpiredAsync(Now.AddHours(2), Ct));

        IReadOnlyList<NotificationState> left = await store.GetAllAsync(Ct);

        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, s => s.MetricKey == "five_hour");
    }

    [Fact]
    public async Task ClearingEverythingEmptiesTheTable()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        await Seed(store);
        await store.ClearAllAsync(Ct);

        Assert.Empty(await store.GetAllAsync(Ct));
        Assert.Equal(0L, temp.CountRows("notification_state"));
    }

    [Fact]
    public async Task TheEvaluatorsStateIsWrittenWholeAndReadBackWhole()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        await Seed(store);

        NotificationState[] next =
        [
            new NotificationState("claude", "five_hour", 80, Now.AddHours(6), Now.AddHours(9)),
            new NotificationState("codex", "codex:10080", 90, Now.AddHours(6), null),
        ];

        await store.ReplaceAllAsync(next, Ct);

        ThresholdState state = await store.LoadThresholdStateAsync(Ct);

        Assert.Equal(next, state.Fired);

        // A restart is a start: the first evaluation after it stays silent.
        Assert.False(state.HasEvaluated);
    }

    [Fact]
    public async Task ReplacingWithNothingEmptiesTheTable()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        await Seed(store);
        await store.ReplaceAllAsync([], Ct);

        Assert.Equal(0L, temp.CountRows("notification_state"));
        Assert.Empty((await store.LoadThresholdStateAsync(Ct)).Fired);
    }

    [Fact]
    public async Task ARowWithAThresholdThatIsNotAPercentageIsIgnoredRatherThanFatal()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        await store.RecordAsync(new NotificationState("claude", "five_hour", 80, Now, Now.AddHours(3)),
                                Ct);

        // Hand-edited, or written by something that is not Altim. A threshold that is not
        // a whole percentage cannot have fired, and reading it must not stop the rest of
        // the table being read — the settings store tolerates nonsense the same way.
        temp.Execute($"""
            INSERT INTO notification_state
                (provider_id, metric_key, threshold, fired_at, window_resets_at)
            VALUES
                ('claude', 'seven_day', 'high', {Now.ToUnixTimeSeconds()}, NULL),
                ('claude', 'seven_day', 10000000000, {Now.ToUnixTimeSeconds()}, NULL),
                ('codex', 'codex:10080', 0, {Now.ToUnixTimeSeconds()}, NULL),
                ('codex', 'codex:60', 80, 'yesterday', NULL);
            """);

        NotificationState usable = Assert.Single(await store.GetAllAsync(Ct));

        Assert.Equal(80, usable.Threshold);
        Assert.Equal("five_hour", usable.MetricKey);
        Assert.Empty(await store.GetAsync("claude", "seven_day", Ct));

        // Ignored, not deleted: clearing still reaches them.
        Assert.Equal(5L, temp.CountRows("notification_state"));
        Assert.Equal(2, await store.ClearMetricAsync("claude", "seven_day", Ct));
    }

    [Fact]
    public async Task ARowWithAnUnusableResetInstantStillLoads()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        temp.Execute($"""
            INSERT INTO notification_state
                (provider_id, metric_key, threshold, fired_at, window_resets_at)
            VALUES ('claude', 'five_hour', 80, {Now.ToUnixTimeSeconds()}, 9223372036854775807);
            """);

        NotificationState stored = Assert.Single(await store.GetAllAsync(Ct));

        // An instant that cannot exist is not an instant. The firing is still known.
        Assert.Null(stored.WindowResetsAt);
        Assert.Equal(Now, stored.FiredAt);
    }

    private static async Task Seed(NotificationStateStore store)
    {
        await store.RecordAsync(new NotificationState("claude", "five_hour", 80, Now, Now.AddHours(3)),
                                Ct);
        await store.RecordAsync(new NotificationState("claude", "five_hour", 95, Now, Now.AddHours(3)),
                                Ct);
        await store.RecordAsync(new NotificationState("claude", "seven_day", 80, Now, Now.AddDays(4)),
                                Ct);
        await store.RecordAsync(new NotificationState("codex", "codex:10080", 80, Now, null), Ct);
    }
}
