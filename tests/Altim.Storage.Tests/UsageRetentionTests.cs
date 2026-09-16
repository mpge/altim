using System.Globalization;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// Old history is thinned, recent history is not, and running the thinning again does
/// nothing at all. Rows are arranged with direct SQL so the timestamps are exact.
/// </summary>
public sealed class UsageRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Cutoff => Now.AddDays(-30).ToUnixTimeSeconds();

    /// <summary>An hour that lies entirely before the cutoff.</summary>
    private static long Bucket => (Cutoff - 7_200) / 3_600 * 3_600;

    /// <summary>The hour before that, used for a bucket that is already one row.</summary>
    private static long AlreadyCollapsed => Bucket - 3_600;

    [Fact]
    public async Task AnHourOfOldRowsBecomesOneRow()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        Arrange(temp);

        RetentionResult result = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);

        Assert.Equal(5, result.CollapsedRows);
        Assert.Equal(2, result.RetainedRows);
        Assert.Equal(4L, temp.CountRows("usage_sample"));

        Assert.Equal(1L, temp.ScalarInt64(
            $"SELECT count(*) FROM usage_sample WHERE captured_at = {Bucket} AND metric_key = 'five_hour'"));
    }

    [Fact]
    public async Task TheHourlyRowKeepsThePeakPercentageAndTheEndOfHourCounters()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        Arrange(temp);
        _ = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);

        string row = $"FROM usage_sample WHERE captured_at = {Bucket} AND metric_key = 'five_hour'";

        // The peak is what old history is for.
        Assert.Equal(55d, temp.ScalarDouble($"SELECT used_percent {row}"));

        // The tokens are a running total, not the work done since the row before, so the
        // hour keeps the last reading of the counter. Summing them (1 + 2 + 3 + 4 = 10)
        // would invent six thousand tokens that were never used.
        Assert.Equal(4L, temp.ScalarInt64($"SELECT input_tokens {row}"));
        Assert.Equal(1L, temp.ScalarInt64($"SELECT output_tokens {row}"));

        // The window and its reset come from the same row as the counters, so the reset
        // instant still belongs to the window the numbers were read in.
        Assert.Equal(300L, temp.ScalarInt64($"SELECT window_minutes {row}"));
        Assert.Equal(Bucket + 7_200, temp.ScalarInt64($"SELECT resets_at {row}"));
    }

    [Fact]
    public async Task ACounterThatResetsMidHourKeepsThePostResetValue()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        // A limit window rolls over inside the hour: the counter drops back and starts
        // again. What the user has used at the end of that hour is 120, not the 2_720 a
        // sum would report.
        temp.Execute($"""
            INSERT INTO usage_sample
                (provider_id, metric_key, captured_at, used_percent, window_minutes, resets_at,
                 input_tokens, output_tokens, cache_read_tokens, cache_write_tokens)
            VALUES
                ('claude', 'five_hour', {Bucket + 60},  80, 300, {Bucket + 600},   900, 800, NULL, NULL),
                ('claude', 'five_hour', {Bucket + 300}, 95, 300, {Bucket + 600},   1700, 1500, NULL, NULL),
                ('claude', 'five_hour', {Bucket + 900}, 5,  300, {Bucket + 18600}, 120, 90, NULL, NULL);
            """);

        _ = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);

        string row = $"FROM usage_sample WHERE captured_at = {Bucket} AND metric_key = 'five_hour'";

        Assert.Equal(95d, temp.ScalarDouble($"SELECT used_percent {row}"));
        Assert.Equal(120L, temp.ScalarInt64($"SELECT input_tokens {row}"));
        Assert.Equal(90L, temp.ScalarInt64($"SELECT output_tokens {row}"));
        Assert.Equal(Bucket + 18_600, temp.ScalarInt64($"SELECT resets_at {row}"));
    }

    [Fact]
    public async Task AnHourNobodyReportedAPercentageForCollapsesToNullNotZero()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        temp.Execute($"""
            INSERT INTO usage_sample
                (provider_id, metric_key, captured_at, used_percent, window_minutes, resets_at,
                 input_tokens, output_tokens, cache_read_tokens, cache_write_tokens)
            VALUES
                ('codex', 'codex:10080', {Bucket + 60},  NULL, 10080, NULL, 5, NULL, NULL, NULL),
                ('codex', 'codex:10080', {Bucket + 120}, NULL, 10080, NULL, 8, NULL, NULL, NULL);
            """);

        _ = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);

        Assert.Equal(DBNull.Value, temp.Scalar(
            $"SELECT used_percent FROM usage_sample WHERE captured_at = {Bucket}"));
        Assert.Equal(8L, temp.ScalarInt64(
            $"SELECT input_tokens FROM usage_sample WHERE captured_at = {Bucket}"));
    }

    [Fact]
    public async Task AnHourStraddlingTheCutoffCollapsesOnlyTheOldHalf()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        // A retention window that does not land on the hour, so one bucket has rows on
        // both sides of the cutoff. Rows younger than the cutoff are not old history yet.
        TimeSpan retentionWindow = TimeSpan.FromDays(30) - TimeSpan.FromMinutes(30);
        long cutoff = Now.Subtract(retentionWindow).ToUnixTimeSeconds();
        long straddled = cutoff / 3_600 * 3_600;

        temp.Execute($"""
            INSERT INTO usage_sample
                (provider_id, metric_key, captured_at, used_percent, window_minutes, resets_at,
                 input_tokens, output_tokens, cache_read_tokens, cache_write_tokens)
            VALUES
                ('claude', 'five_hour', {straddled + 60},  10, 300, NULL, 1, NULL, NULL, NULL),
                ('claude', 'five_hour', {straddled + 120}, 20, 300, NULL, 2, NULL, NULL, NULL),
                ('claude', 'five_hour', {cutoff + 60},     30, 300, NULL, 3, NULL, NULL, NULL),
                ('claude', 'five_hour', {cutoff + 120},    40, 300, NULL, 4, NULL, NULL, NULL);
            """);

        RetentionResult result = await retention.DownsampleAsync(Now, retentionWindow, Ct);

        Assert.Equal(2, result.CollapsedRows);
        Assert.Equal(1, result.RetainedRows);

        // The two rows on the young side of the cutoff are full-resolution history and
        // are still there, at full resolution.
        Assert.Equal(20d, temp.ScalarDouble(
            $"SELECT used_percent FROM usage_sample WHERE captured_at = {straddled}"));
        Assert.Equal(2L, temp.ScalarInt64(
            $"SELECT input_tokens FROM usage_sample WHERE captured_at = {straddled}"));
        Assert.Equal(30d, temp.ScalarDouble(
            $"SELECT used_percent FROM usage_sample WHERE captured_at = {cutoff + 60}"));
        Assert.Equal(40d, temp.ScalarDouble(
            $"SELECT used_percent FROM usage_sample WHERE captured_at = {cutoff + 120}"));
        Assert.Equal(3L, temp.CountRows("usage_sample"));
    }

    [Fact]
    public async Task RecentRowsAreLeftExactlyAsTheyWere()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        Arrange(temp);
        long recent = Now.AddMinutes(-90).ToUnixTimeSeconds();

        _ = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);

        Assert.Equal(1L, temp.ScalarInt64(
            $"SELECT count(*) FROM usage_sample WHERE captured_at = {recent}"));
        Assert.Equal(77d, temp.ScalarDouble(
            $"SELECT used_percent FROM usage_sample WHERE captured_at = {recent}"));
    }

    [Fact]
    public async Task AnHourThatIsAlreadyOneRowOnTheHourIsNotTouched()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        Arrange(temp);
        _ = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);

        Assert.Equal(5d, temp.ScalarDouble(
            $"SELECT used_percent FROM usage_sample WHERE captured_at = {AlreadyCollapsed}"));
        Assert.Equal(9L, temp.ScalarInt64(
            $"SELECT input_tokens FROM usage_sample WHERE captured_at = {AlreadyCollapsed}"));
    }

    [Fact]
    public async Task RunningItAgainChangesNothing()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        Arrange(temp);
        _ = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);

        long before = temp.CountRows("usage_sample");
        RetentionResult second = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);
        RetentionResult third = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);

        Assert.True(second.ChangedNothing);
        Assert.True(third.ChangedNothing);
        Assert.Equal(before, temp.CountRows("usage_sample"));
    }

    [Fact]
    public async Task DownsamplingAnEmptyDatabaseChangesNothing()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        RetentionResult result = await retention.DownsampleAsync(Now, Ct);

        Assert.True(result.ChangedNothing);
        Assert.Equal(0L, temp.CountRows("usage_sample"));
    }

    [Fact]
    public async Task TheFirstCompactionCallOnlyStartsTheClock()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var retention = new UsageRetention(database);
        var settings = new SqliteSettingsStore(database);

        Assert.False(await retention.VacuumIfDueAsync(Now, TimeSpan.FromDays(30), Ct));
        Assert.Equal("1777636800", await settings.GetValueAsync(UsageRetention.LastVacuumKey, Ct));
    }

    [Fact]
    public async Task CompactionWaitsForTheIntervalAndThenRuns()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());
        TimeSpan interval = TimeSpan.FromDays(30);

        Assert.False(await retention.VacuumIfDueAsync(Now, interval, Ct));
        Assert.False(await retention.VacuumIfDueAsync(Now.AddDays(29), interval, Ct));
        Assert.True(await retention.VacuumIfDueAsync(Now.AddDays(31), interval, Ct));
        Assert.False(await retention.VacuumIfDueAsync(Now.AddDays(32), interval, Ct));
        Assert.True(await retention.VacuumIfDueAsync(Now.AddDays(62), interval, Ct));
    }

    [Fact]
    public async Task ACorruptCompactionMarkerIsReplacedRatherThanTrusted()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var retention = new UsageRetention(database);
        var settings = new SqliteSettingsStore(database);

        await settings.SetValueAsync(UsageRetention.LastVacuumKey, "whenever", Ct);

        Assert.False(await retention.VacuumIfDueAsync(Now, TimeSpan.FromDays(30), Ct));
        Assert.Equal("1777636800", await settings.GetValueAsync(UsageRetention.LastVacuumKey, Ct));
    }

    [Fact]
    public async Task ACompactionMarkerDatedInTheFutureIsReplacedRatherThanTrusted()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var retention = new UsageRetention(database);
        var settings = new SqliteSettingsStore(database);
        TimeSpan interval = TimeSpan.FromDays(30);

        // A clock that was wrong once — a bad system time, a restore from another
        // machine — must not defer compaction for as long as the marker says.
        await settings.SetValueAsync(UsageRetention.LastVacuumKey,
                                     Now.AddYears(5).ToUnixTimeSeconds().ToString(
                                         CultureInfo.InvariantCulture), Ct);

        Assert.False(await retention.VacuumIfDueAsync(Now, interval, Ct));
        Assert.Equal("1777636800", await settings.GetValueAsync(UsageRetention.LastVacuumKey, Ct));

        // And the clock it restarted is a real one.
        Assert.True(await retention.VacuumIfDueAsync(Now.AddDays(31), interval, Ct));
    }

    [Fact]
    public async Task MaintenanceDoesNotRunOnTheCallingThread()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var retention = new UsageRetention(database);

        Arrange(temp);

        // Collapsing a month of history and rewriting the whole file are the two heaviest
        // things Altim does to its database, and neither may run on the caller — a
        // dashboard opening a chart would freeze for the duration.
        //
        // Holding the writer is what makes this deterministic. Each of these needs it, so
        // while the lease is held none of them can finish; a task that is still pending
        // therefore proves the work left this thread. Asserting "not yet completed" without
        // the lease is a race the machine wins whenever it is fast enough, which is exactly
        // how this test passed on a developer machine and failed on a CI runner.
        ValueTask<RetentionResult> downsampling;
        ValueTask compacting;
        ValueTask<bool> due;

        using (await database.LeaseWriterAsync(Ct))
        {
            downsampling = retention.DownsampleAsync(Now, Ct);
            compacting = retention.VacuumAsync(Ct);
            due = retention.VacuumIfDueAsync(Now, TimeSpan.FromDays(30), Ct);

            Assert.False(downsampling.IsCompleted);
            Assert.False(compacting.IsCompleted);
            Assert.False(due.IsCompleted);
        }

        _ = await downsampling;
        await compacting;
        _ = await due;
    }

    [Fact]
    public async Task CompactingOnDemandKeepsEveryRow()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        Arrange(temp);
        long before = temp.CountRows("usage_sample");

        await retention.VacuumAsync(Ct);

        Assert.Equal(before, temp.CountRows("usage_sample"));
    }

    /// <summary>
    /// One hour before the cutoff holding four readings of one metric and one of
    /// another, an earlier hour already reduced to a single row sitting on the hour, and
    /// one recent reading that must survive untouched.
    /// </summary>
    private static void Arrange(TempDatabase temp)
    {
        long recent = Now.AddMinutes(-90).ToUnixTimeSeconds();

        temp.Execute($"""
            INSERT INTO usage_sample
                (provider_id, metric_key, captured_at, used_percent, window_minutes, resets_at,
                 input_tokens, output_tokens, cache_read_tokens, cache_write_tokens)
            VALUES
                ('claude', 'five_hour',  {Bucket + 60},  10,   300, {Bucket + 3600}, 1, 1, NULL, NULL),
                ('claude', 'five_hour',  {Bucket + 120}, 55,   300, {Bucket + 3600}, 2, 1, NULL, NULL),
                ('claude', 'five_hour',  {Bucket + 180}, 30,   300, {Bucket + 7200}, 3, 1, NULL, NULL),
                ('claude', 'five_hour',  {Bucket + 240}, NULL, 300, {Bucket + 7200}, 4, 1, NULL, NULL),
                ('claude', 'seven_day',  {Bucket + 60},  90,   10080, NULL, 7, NULL, NULL, NULL),
                ('claude', 'five_hour',  {AlreadyCollapsed}, 5, 300, NULL, 9, NULL, NULL, NULL),
                ('claude', 'five_hour',  {recent}, 77, 300, NULL, 100, NULL, NULL, NULL);
            """);
    }
}
