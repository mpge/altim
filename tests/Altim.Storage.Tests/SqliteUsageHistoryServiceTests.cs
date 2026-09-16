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

    [Fact]
    public async Task ReadingHistoryDoesNotRunOnTheCallingThread()
    {
        using var temp = new TempDatabase();
        var history = new SqliteUsageHistoryService(temp.Open());

        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);

        // A month of history on the dashboard is the case that matters, and the dashboard
        // asks from the UI thread.
        ValueTask<IReadOnlyList<UsageSample>> range =
            history.GetRangeAsync("claude", Origin.AddDays(-30), Origin.AddDays(1), Ct);
        Assert.False(range.IsCompleted);
        _ = Assert.Single(await range);

        ValueTask<IReadOnlyList<UsageSample>> carryIn =
            history.GetLatestBeforeAsync("claude", Origin.AddDays(1), Ct);
        Assert.False(carryIn.IsCompleted);
        _ = Assert.Single(await carryIn);
    }

    private static ValueTask<IReadOnlyList<UsageSample>> Range(
        SqliteUsageHistoryService history, DateTimeOffset from, DateTimeOffset to)
        => history.GetRangeAsync("claude", from, to, Ct);

    /// <summary>
    /// A reading that did not move writes no row, and — the part this asserts — does not
    /// take the writer either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison used to happen inside a write transaction, which is invisible in the
    /// file and very visible in the maintenance pass: <see cref="AltimDatabase"/> marks the
    /// database as recently written when the writer is <em>taken</em>, not when something is
    /// written to it. So readings arriving once a minute meant two minutes never passed
    /// without a write, and the checkpoint that empties the write-ahead log never ran while
    /// an agent was working — which is the only time the log grows.
    /// </para>
    /// <para>
    /// Asserted through <see cref="AltimDatabase.CheckpointIfIdleAsync"/>, which is the
    /// caller that actually cares, with a control either side: a reading that did move is
    /// still recent enough to refuse the checkpoint.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReadingThatDidNotMoveDoesNotTakeTheWriter()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var history = new SqliteUsageHistoryService(database);

        TimeSpan idleWindow = TimeSpan.FromMilliseconds(250);

        // The control: a reading that moved has just taken the writer, so the database is
        // not idle and the checkpoint is refused.
        await history.RecordAsync(Usage(Origin, Metric("five_hour", 41.5)), Ct);
        Assert.Equal(WalCheckpoint.Skipped, await database.CheckpointIfIdleAsync(idleWindow, Ct));

        // Well clear of the window, so anything that takes the writer from here resets it.
        await Task.Delay(TimeSpan.FromMilliseconds(600), Ct);

        await history.RecordAsync(Usage(Origin.AddMinutes(1), Metric("five_hour", 41.5)), Ct);

        Assert.NotEqual(WalCheckpoint.Skipped, await database.CheckpointIfIdleAsync(idleWindow, Ct));
        Assert.Equal(1L, temp.CountRows("usage_sample"));
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
}
