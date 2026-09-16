using Altim.Core.Notifications;
using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// The state the evaluator produces is identical on almost every reading: nothing has
    /// crossed a threshold and no window has rolled over. Rewriting it anyway is a delete
    /// plus an insert per row inside a write transaction, several times a minute while an
    /// agent works.
    /// </summary>
    /// <remarks>
    /// <c>PRAGMA data_version</c> is read twice on one connection that stays open across the
    /// calls, which is the only way SQLite defines it: it changes when <em>another</em>
    /// connection commits. So this asserts the file was not modified, rather than taking the
    /// store's word for it.
    /// </remarks>
    [Fact]
    public async Task AnUnchangedStateIsNotWrittenAgain()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var store = new NotificationStateStore(database);

        NotificationState[] fired =
        [
            new NotificationState("claude", "five_hour", 80, Now, Now.AddHours(3)),
            new NotificationState("codex", "codex:10080", 90, Now, null),
        ];

        Assert.True(await store.ReplaceAllAsync(fired, Ct));

        using SqliteConnection watcher = database.OpenRead();
        long before = DataVersion(watcher);

        Assert.False(await store.ReplaceAllAsync(fired, Ct));
        Assert.False(await store.ReplaceAllAsync([.. fired], Ct));

        Assert.Equal(before, DataVersion(watcher));
        Assert.Equal(2L, temp.CountRows("notification_state"));

        // And the same connection does see a real write, so the reading above is evidence
        // rather than a pragma that never moves.
        Assert.True(await store.ReplaceAllAsync([fired[0]], Ct));
        Assert.NotEqual(before, DataVersion(watcher));
    }

    /// <summary>Reads SQLite's own "somebody else committed" counter.</summary>
    /// <param name="connection">A connection held open across the calls being measured.</param>
    private static long DataVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// And a state that has changed is still written, in every direction a change can go:
    /// one more entry, one fewer, and the same entries with a different value on one.
    /// </summary>
    [Fact]
    public async Task AChangedStateIsWritten()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        var first = new NotificationState("claude", "five_hour", 80, Now, Now.AddHours(3));
        var second = new NotificationState("codex", "codex:10080", 90, Now, null);

        Assert.True(await store.ReplaceAllAsync([first], Ct));
        Assert.True(await store.ReplaceAllAsync([first, second], Ct));
        Assert.True(await store.ReplaceAllAsync([first with { WindowResetsAt = Now.AddHours(4) }, second], Ct));
        Assert.True(await store.ReplaceAllAsync([second], Ct));
        Assert.True(await store.ReplaceAllAsync([], Ct));

        Assert.Equal(0L, temp.CountRows("notification_state"));
    }

    /// <summary>
    /// The skip is only ever about what this process wrote. Anything that changes the table
    /// by another route gives the answer up, so the next replacement rewrites the table
    /// rather than trusting a state that is no longer on file.
    /// </summary>
    [Fact]
    public async Task AWriteByAnotherRouteMakesTheNextReplacementWriteAgain()
    {
        using var temp = new TempDatabase();
        var store = new NotificationStateStore(temp.Open());

        NotificationState[] fired = [new NotificationState("claude", "five_hour", 80, Now, Now.AddHours(3))];

        Assert.True(await store.ReplaceAllAsync(fired, Ct));
        Assert.False(await store.ReplaceAllAsync(fired, Ct));

        await store.ClearAllAsync(Ct);

        Assert.True(await store.ReplaceAllAsync(fired, Ct));
        Assert.Equal(1L, temp.CountRows("notification_state"));
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
