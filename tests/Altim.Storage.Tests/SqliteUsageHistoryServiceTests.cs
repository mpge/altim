using Altim.Core.Models;
using Altim.Core.Notifications;
using Altim.Core.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// History is only worth keeping if it is honest: no row for a reading that did not
/// move, no zero standing in for an unknown, and no sample from outside the range that
/// was asked for.
/// </summary>
public sealed class SqliteUsageHistoryServiceTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFirstReadingIsRecorded()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);

        UsageSample sample = Assert.Single(await Range(history, Origin, Origin.AddHours(1)));

        Assert.Equal("claude", sample.ProviderId);
        Assert.Equal("five_hour", sample.MetricKey);
        Assert.Equal(Origin, sample.CapturedAt);
        Assert.Equal(41.5, sample.UsedPercent);
    }

    [Fact]
    public async Task AReadingThatHasNotMovedWritesNothing()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(1), Metric("five_hour", 41.5)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(2), Metric("five_hour", 41.5)), Ct);

        Assert.Equal(1L, temp.CountRows("usage_sample"));
    }

    [Fact]
    public async Task AReadingThatMovedWritesANewRow()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(1), Metric("five_hour", 41.5)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(2), Metric("five_hour", 42.0)), Ct);

        IReadOnlyList<UsageSample> samples = await Range(history, Origin, Origin.AddHours(1));

        Assert.Equal(2, samples.Count);
        Assert.Equal(41.5, samples[0].UsedPercent);
        Assert.Equal(42.0, samples[1].UsedPercent);
        Assert.Equal(Origin.AddMinutes(2), samples[1].CapturedAt);
    }

    [Fact]
    public async Task AChangeInTokensAloneWritesARow()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(
            Usage(Origin, new TokenTotals(10, 5, null, null), Metric("five_hour", 41.5)), Ct);
        await history.RecordAsync(
            Usage(Origin.AddMinutes(1), new TokenTotals(12, 5, null, null), Metric("five_hour", 41.5)),
            Ct);

        Assert.Equal(2L, temp.CountRows("usage_sample"));
    }

    [Fact]
    public async Task AnUnreportedPercentIsStoredAsNullNotZero()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("seven_day", null)), Ct);

        UsageSample sample = Assert.Single(await Range(history, Origin, Origin.AddHours(1)));

        Assert.Null(sample.UsedPercent);
        Assert.Null(sample.WindowLength);
        Assert.Null(sample.ResetsAt);
        Assert.Null(sample.Tokens);
        Assert.Equal(DBNull.Value, temp.Scalar("SELECT used_percent FROM usage_sample"));
    }

    [Fact]
    public async Task AnImplausiblePercentIsStoredAsUnknown()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        // The known provider defect: a timestamp arriving in the percentage field.
        await history.RecordAsync(Usage(Origin, Metric("five_hour", 1_763_000_000d)), Ct);

        UsageSample sample = Assert.Single(await Range(history, Origin, Origin.AddHours(1)));
        Assert.Null(sample.UsedPercent);
    }

    [Fact]
    public async Task AValueBecomingUnknownIsAChange()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(1), Metric("five_hour", null)), Ct);

        IReadOnlyList<UsageSample> samples = await Range(history, Origin, Origin.AddHours(1));

        Assert.Equal(2, samples.Count);
        Assert.Null(samples[1].UsedPercent);
    }

    [Fact]
    public async Task AValueBecomingKnownIsAChangeToo()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", null)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(1), Metric("five_hour", 41.5)), Ct);

        IReadOnlyList<UsageSample> samples = await Range(history, Origin, Origin.AddHours(1));

        Assert.Equal(2, samples.Count);
        Assert.Null(samples[0].UsedPercent);
        Assert.Equal(41.5, samples[1].UsedPercent);
    }

    [Fact]
    public async Task AFailedReadingIsNotRecordedAsData()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);

        // A read that failed knows nothing. Writing its metrics would put a hole in the
        // history that later reads cannot tell apart from a measured drop to nothing.
        await history.RecordAsync(
            new ProviderUsage("claude", ProviderStatus.Error, [Metric("five_hour", null)], null,
                              Origin.AddMinutes(1), "Unable to retrieve usage"),
            Ct);

        UsageSample only = Assert.Single(await Range(history, Origin, Origin.AddHours(1)));

        Assert.Equal(41.5, only.UsedPercent);
        Assert.Equal(1L, temp.CountRows("usage_sample"));
    }

    [Fact]
    public async Task AFailedReadingCarryingNumbersIsStillNotRecorded()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(
            new ProviderUsage("claude", ProviderStatus.Error, [Metric("five_hour", 41.5)],
                              new TokenTotals(10, 5, null, null), Origin, "Unable to retrieve usage"),
            Ct);

        Assert.Equal(0L, temp.CountRows("usage_sample"));
    }

    [Fact]
    public async Task OneMetricFailingToWriteLeavesNoneOfTheReadingBehind()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        // Make the second metric's insert fail, in the one way a test can arrange from
        // outside: the reading is written as one transaction, so all of it or none.
        temp.Execute("""
            CREATE TRIGGER refuse_seven_day BEFORE INSERT ON usage_sample
            WHEN NEW.metric_key = 'seven_day'
            BEGIN SELECT RAISE(ABORT, 'refused'); END;
            """);

        _ = await Assert.ThrowsAsync<SqliteException>(async () => await history.RecordAsync(
            Usage(Origin, Metric("five_hour", 41.5), Metric("seven_day", 12.5)), Ct));

        Assert.Equal(0L, temp.CountRows("usage_sample"));
        Assert.Empty(await Range(history, Origin, Origin.AddHours(1)));
    }

    [Fact]
    public async Task TheWindowAndItsResetRoundTrip()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        DateTimeOffset resets = Origin.AddHours(3);
        await history.RecordAsync(
            Usage(Origin, Metric("five_hour", 41.5, TimeSpan.FromHours(5), resets)), Ct);

        UsageSample sample = Assert.Single(await Range(history, Origin, Origin.AddHours(1)));

        Assert.Equal(TimeSpan.FromHours(5), sample.WindowLength);
        Assert.Equal(resets, sample.ResetsAt);
    }

    [Fact]
    public async Task TokensRoundTripIncludingTheComponentsThatAreNotReported()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(
            Usage(Origin, new TokenTotals(1_000, 250, null, 40), Metric("five_hour", 41.5)), Ct);

        UsageSample sample = Assert.Single(await Range(history, Origin, Origin.AddHours(1)));
        TokenTotals tokens = Assert.IsType<TokenTotals>(sample.Tokens);

        Assert.Equal(1_000L, tokens.Input);
        Assert.Equal(250L, tokens.Output);
        Assert.Null(tokens.CacheRead);
        Assert.Equal(40L, tokens.CacheWrite);
    }

    [Fact]
    public async Task ARangeIncludesItsLowerBoundAndExcludesItsUpper()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 10)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(1), Metric("five_hour", 20)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(2), Metric("five_hour", 30)), Ct);

        IReadOnlyList<UsageSample> window =
            await Range(history, Origin.AddMinutes(1), Origin.AddMinutes(2));

        UsageSample only = Assert.Single(window);
        Assert.Equal(20d, only.UsedPercent);
        Assert.Equal(Origin.AddMinutes(1), only.CapturedAt);
    }

    [Fact]
    public async Task SamplesComeBackOldestFirstHoweverTheyWereWritten()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin.AddMinutes(10), Metric("five_hour", 30)), Ct);
        await history.RecordAsync(Usage(Origin, Metric("five_hour", 10)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(5), Metric("five_hour", 20)), Ct);

        IReadOnlyList<UsageSample> samples = await Range(history, Origin, Origin.AddHours(1));

        DateTimeOffset[] expected = [Origin, Origin.AddMinutes(5), Origin.AddMinutes(10)];
        Assert.Equal(expected, samples.Select(s => s.CapturedAt).ToArray());
    }

    [Fact]
    public async Task OneProvidersHistoryDoesNotContainAnothers()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 10)), Ct);
        await history.RecordAsync(
            new ProviderUsage("codex", ProviderStatus.Idle, [Metric("codex:10080", 70)], null, Origin,
                              null),
            Ct);

        UsageSample claude = Assert.Single(await Range(history, Origin, Origin.AddHours(1)));
        Assert.Equal("claude", claude.ProviderId);

        UsageSample codex = Assert.Single(
            await history.GetRangeAsync("codex", Origin, Origin.AddHours(1), Ct));
        Assert.Equal("codex:10080", codex.MetricKey);
    }

    [Fact]
    public async Task AReadingWithNoMetricsWritesNothing()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(
            new ProviderUsage("claude", ProviderStatus.NotDetected, [], null, Origin, "Not installed"),
            Ct);

        Assert.Equal(0L, temp.CountRows("usage_sample"));
    }

    [Fact]
    public async Task AReadingWithNoTimestampIsStampedFromTheClock()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open(), new FixedClock(Origin));

        await history.RecordAsync(
            new ProviderUsage("claude", ProviderStatus.Idle, [Metric("five_hour", 41.5)], null, null,
                              null),
            Ct);

        UsageSample sample = Assert.Single(await Range(history, Origin, Origin.AddHours(1)));
        Assert.Equal(Origin, sample.CapturedAt);
    }

    [Fact]
    public async Task ClearingHistoryLeavesSettingsAndNotificationStateAlone()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        var history = new SqliteUsageHistoryService(database);
        var settings = new SqliteSettingsStore(database);
        var notifications = new NotificationStateStore(database);

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);
        await settings.SaveAsync(AltimSettings.Default with { NotificationsEnabled = false }, Ct);
        await notifications.RecordAsync(
            new NotificationState("claude", "five_hour", 80, Origin, Origin.AddHours(2)), Ct);

        await history.ClearAsync(Ct);

        Assert.Equal(0L, temp.CountRows("usage_sample"));
        Assert.False((await settings.GetAsync(Ct)).NotificationsEnabled);
        _ = Assert.Single(await notifications.GetAllAsync(Ct));
        Assert.Equal(1L, temp.CountRows("schema_version"));
    }

    [Fact]
    public async Task AQuietDayStillKnowsWhatTheValueWas()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        // The last change was 25 hours ago, so a 24-hour chart's range is empty. That is
        // not "no usage recorded yet", it is "nothing moved", and the carry-in says so.
        await history.RecordAsync(Usage(Origin.AddHours(-25), Metric("five_hour", 41.5)), Ct);

        Assert.Empty(await Range(history, Origin.AddHours(-24), Origin));

        UsageSample carriedIn = Assert.Single(
            await history.GetLatestBeforeAsync("claude", Origin.AddHours(-24), Ct));

        Assert.Equal(41.5, carriedIn.UsedPercent);
        Assert.Equal(Origin.AddHours(-25), carriedIn.CapturedAt);
    }

    [Fact]
    public async Task TheCarryInIsTheNewestSampleOfEveryMetric()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(
            Usage(Origin, Metric("five_hour", 10), Metric("seven_day", 60)), Ct);
        await history.RecordAsync(
            Usage(Origin.AddMinutes(5), Metric("five_hour", 20), Metric("seven_day", 60)), Ct);
        await history.RecordAsync(
            Usage(Origin.AddMinutes(9), Metric("five_hour", 30), Metric("seven_day", 61)), Ct);

        IReadOnlyList<UsageSample> carriedIn =
            await history.GetLatestBeforeAsync("claude", Origin.AddMinutes(10), Ct);

        Assert.Equal(new[] { "five_hour", "seven_day" },
                     carriedIn.Select(s => s.MetricKey).ToArray());
        Assert.Equal(30d, carriedIn[0].UsedPercent);
        Assert.Equal(61d, carriedIn[1].UsedPercent);
    }

    [Fact]
    public async Task TheCarryInStopsStrictlyBeforeTheInstantAsked()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 10)), Ct);
        await history.RecordAsync(Usage(Origin.AddMinutes(5), Metric("five_hour", 20)), Ct);

        // The sample sitting exactly on the boundary belongs to the range, not to the
        // carry-in, so combining the two never draws the same row twice.
        UsageSample carriedIn = Assert.Single(
            await history.GetLatestBeforeAsync("claude", Origin.AddMinutes(5), Ct));

        Assert.Equal(10d, carriedIn.UsedPercent);

        UsageSample inRange = Assert.Single(
            await Range(history, Origin.AddMinutes(5), Origin.AddHours(1)));

        Assert.Equal(20d, inRange.UsedPercent);
    }

    [Fact]
    public async Task ThereIsNoCarryInBeforeTheFirstReadingOrForAnotherProvider()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 10)), Ct);

        Assert.Empty(await history.GetLatestBeforeAsync("claude", Origin, Ct));
        Assert.Empty(await history.GetLatestBeforeAsync("codex", Origin.AddHours(1), Ct));
    }

    [Fact]
    public async Task TheCarryInRoundTripsEverythingASampleCarries()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        DateTimeOffset resets = Origin.AddHours(3);
        await history.RecordAsync(
            Usage(Origin, new TokenTotals(1_000, 250, null, 40),
                  Metric("five_hour", 41.5, TimeSpan.FromHours(5), resets)), Ct);

        UsageSample carriedIn = Assert.Single(
            await history.GetLatestBeforeAsync("claude", Origin.AddMinutes(1), Ct));

        Assert.Equal("claude", carriedIn.ProviderId);
        Assert.Equal(TimeSpan.FromHours(5), carriedIn.WindowLength);
        Assert.Equal(resets, carriedIn.ResetsAt);
        Assert.Equal(1_000L, Assert.IsType<TokenTotals>(carriedIn.Tokens).Input);
    }

    /// <summary>
    /// Neither read runs on the thread that asked for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A month of history on the dashboard is the case that matters, and the dashboard asks
    /// from the UI thread. Microsoft.Data.Sqlite runs every statement inline, so whichever
    /// thread asks is the one that would wear the scan.
    /// </para>
    /// <para>
    /// This used to assert that the returned task had not completed yet, which proves
    /// nothing: it is a bet that the thread pool has not finished the read before the next
    /// line runs, and an idle runner wins that bet. It failed on CI for no defect at all.
    /// </para>
    /// <para>
    /// Nothing below depends on how long anything takes. A read in write-ahead log mode is
    /// never blocked, which is what the mode is for, so the file is put back on a rollback
    /// journal and an exclusive transaction is held on the writer: while it is held, no read
    /// of this database can finish, on any thread. The calls are then made on a thread of
    /// this test's own, which parks on their results under a
    /// <see cref="SynchronizationContext"/> that runs nothing posted to it. Three things
    /// follow, and each of them fails as a timeout rather than as a hang:
    /// </para>
    /// <para>
    /// The calls come back while no read can finish, so neither of them did its reading on
    /// that thread. The results do not arrive while the transaction is held, which is what
    /// says the hold is real and the first point means something. And the results do arrive
    /// once it is released, although the thread that asked is parked and its context is
    /// running nothing, so the work needed neither of them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReadingHistoryDoesNotRunOnTheCallingThread()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var history = new SqliteUsageHistoryService(database);

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);

        // Captured here and handed over: the calls below are made on a thread of this
        // test's own, which is no place to go looking for the ambient test context.
        CancellationToken ct = Ct;
        var caller = new ParkedCaller();
        Task<IReadOnlyList<UsageSample>[]> reads;

        using (WriteLease lease = await database.LeaseWriterAsync(ct))
        using (HoldEveryRead(lease.Connection))
        {
            reads = caller.Run(
                () => history.GetRangeAsync("claude", Origin.AddDays(-30), Origin.AddDays(1), ct),
                () => history.GetLatestBeforeAsync("claude", Origin.AddDays(1), ct));

            // Throws a TimeoutException if the calls did not come back, which is the failure
            // this test is for: a read that occupied the thread it was asked from would
            // still be inside the first call. The budget is a deadlock guard rather than
            // part of the assertion, so it is generous enough to survive a loaded machine.
            await caller.Called.WaitAsync(TimeSpan.FromSeconds(30), ct);

            // And the hold has to be real, or the line above proves nothing at all.
            Assert.False(reads.IsCompleted);
        }

        IReadOnlyList<UsageSample>[] read = await reads.WaitAsync(TimeSpan.FromSeconds(30), ct);

        _ = Assert.Single(read[0]);
        _ = Assert.Single(read[1]);
    }

    private static ValueTask<IReadOnlyList<UsageSample>> Range(
        SqliteUsageHistoryService history, DateTimeOffset from, DateTimeOffset to)
        => history.GetRangeAsync("claude", from, to, Ct);

    /// <summary>
    /// A reading that did not move writes no row, and does not ask for the writer to find
    /// that out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison used to happen inside a write transaction. Most readings leave every
    /// metric exactly where it was, so most readings opened a transaction, found nothing to
    /// do and committed nothing — while holding the one writer the whole process shares, and
    /// queueing behind them anything else that wanted it.
    /// </para>
    /// <para>
    /// Asserted by holding the writer from here: an unchanged reading has to finish anyway,
    /// and a changed one cannot. Nothing in this depends on how long anything takes, only on
    /// whether it can complete while the writer is held somewhere else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReadingThatDidNotMoveDoesNotTakeTheWriter()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var history = new SqliteUsageHistoryService(database);

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);

        Task changed;
        using (WriteLease held = await database.LeaseWriterAsync(Ct))
        {
            // The same value again: the read says there is nothing to write, so the writer
            // is never asked for and this finishes while somebody else holds it.
            Task unchanged = history
                .RecordAsync(Usage(Origin.AddMinutes(1), Metric("five_hour", 41.5)), Ct)
                .AsTask();

            // Throws a TimeoutException if it did not finish, which is the failure this
            // test is for: an unchanged reading waiting for a writer it has no use for.
            // The budget is a deadlock guard rather than part of the assertion — the proof
            // is that it finishes while somebody else holds the writer, not that it
            // finishes quickly — so it is generous enough to survive a loaded machine.
            await unchanged.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // The control: a value that moved does need the writer, and waits for it.
            changed = history
                .RecordAsync(Usage(Origin.AddMinutes(2), Metric("five_hour", 42.0)), Ct)
                .AsTask();

            // And the control has to be blocked, or the assertion above proves nothing.
            _ = await Assert.ThrowsAsync<TimeoutException>(
                async () => await changed.WaitAsync(TimeSpan.FromMilliseconds(300), Ct));
        }

        await changed;

        IReadOnlyList<UsageSample> samples = await Range(history, Origin, Origin.AddHours(1));

        Assert.Equal(2, samples.Count);
        Assert.Equal(42.0, samples[1].UsedPercent);
    }

    /// <summary>
    /// Stops any read of this database finishing until the returned handle is disposed.
    /// </summary>
    /// <param name="writer">
    /// The write connection, which the caller holds the lease on: the journal mode is
    /// changed underneath it, so nothing else in the process may be using it.
    /// </param>
    /// <remarks>
    /// A read in write-ahead log mode is never blocked by a writer, so a write transaction
    /// would hold nothing up. The file goes back on a rollback journal, where an exclusive
    /// transaction does lock every other connection out, including the short-lived ones the
    /// read path opens for itself. They wait rather than fail: the read connections carry a
    /// busy timeout and a command timeout measured in seconds, and this is released in
    /// microseconds.
    /// </remarks>
    private static IDisposable HoldEveryRead(SqliteConnection writer) => new HeldReads(writer);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static ProviderUsage Usage(DateTimeOffset at, params UsageMetric[] metrics)
        => Usage(at, tokens: null, metrics);

    private static ProviderUsage Usage(DateTimeOffset at, TokenTotals? tokens,
                                       params UsageMetric[] metrics)
        => new("claude", ProviderStatus.Idle, metrics, tokens, at, null);

    private static UsageMetric Metric(string key, double? percent, TimeSpan? window = null,
                                      DateTimeOffset? resets = null)
        => new(key, "Session", percent,
               window is null ? null : new LimitWindow(window.Value, resets),
               MetricConfidence.Documented);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class HeldReads : IDisposable
    {
        private readonly SqliteConnection _writer;

        public HeldReads(SqliteConnection writer)
        {
            _writer = writer;
            Execute(writer, "PRAGMA journal_mode = DELETE");
            Execute(writer, "BEGIN EXCLUSIVE");
        }

        // Left on a rollback journal deliberately: switching back needs the file to itself,
        // and the reads this just let go of are still finishing.
        public void Dispose() => Execute(_writer, "COMMIT");
    }

    /// <summary>
    /// A thread standing in for the one a dashboard asks from. It makes the calls, says when
    /// they came back, and then parks on their results with a context that runs nothing
    /// posted to it, so work that needed this thread or its context could never finish.
    /// </summary>
    /// <remarks>
    /// Parking is a plain blocking wait, and deliberately on a thread of its own: the thread
    /// pool can hand a task back to a waiter that is one of its own threads and run it
    /// there, which would put the work on the caller after all and prove the opposite of
    /// what this is for.
    /// </remarks>
    private sealed class ParkedCaller
    {
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once every call has returned, before anything is waited on.</summary>
        public Task Called => _called.Task;

        public Task<IReadOnlyList<UsageSample>[]> Run(
            params Func<ValueTask<IReadOnlyList<UsageSample>>>[] calls)
        {
            var finished = new TaskCompletionSource<IReadOnlyList<UsageSample>[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new ParkedContext());

                    var pending = new Task<IReadOnlyList<UsageSample>>[calls.Length];
                    for (int i = 0; i < calls.Length; i++)
                    {
                        pending[i] = calls[i]().AsTask();
                    }

                    _ = _called.TrySetResult();

                    var read = new IReadOnlyList<UsageSample>[pending.Length];
                    for (int i = 0; i < pending.Length; i++)
                    {
                        read[i] = pending[i].GetAwaiter().GetResult();
                    }

                    finished.SetResult(read);
                }
                catch (Exception error)
                {
                    // Whichever of the two the test is waiting on, it learns what happened
                    // instead of timing out on it.
                    _ = _called.TrySetException(error);
                    finished.SetException(error);
                }
            })
            {
                IsBackground = true,
                Name = "altim-history-caller",
            };

            thread.Start();
            return finished.Task;
        }
    }

    private sealed class ParkedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Dropped. A continuation that has to come back to a parked thread never runs,
            // which is the deadlock this stands in for.
        }

        public override void Send(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException("Nothing runs on the parked thread.");
    }
}
