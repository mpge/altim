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
    public async Task TheHourlyRowKeepsTheHighestPercentageAndTheSummedTokens()
    {
        using var temp = new TempDatabase();
        var retention = new UsageRetention(temp.Open());

        Arrange(temp);
        _ = await retention.DownsampleAsync(Now, TimeSpan.FromDays(30), Ct);

        string row = $"FROM usage_sample WHERE captured_at = {Bucket} AND metric_key = 'five_hour'";

        Assert.Equal(55d, temp.ScalarDouble($"SELECT used_percent {row}"));
        Assert.Equal(6L, temp.ScalarInt64($"SELECT input_tokens {row}"));
        Assert.Equal(300L, temp.ScalarInt64($"SELECT window_minutes {row}"));
        Assert.Equal(Bucket + 7_200, temp.ScalarInt64($"SELECT resets_at {row}"));
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
                ('claude', 'five_hour',  {Bucket + 240}, NULL, 300, {Bucket + 7200}, NULL, NULL, NULL, NULL),
                ('claude', 'seven_day',  {Bucket + 60},  90,   10080, NULL, 7, NULL, NULL, NULL),
                ('claude', 'five_hour',  {AlreadyCollapsed}, 5, 300, NULL, 9, NULL, NULL, NULL),
                ('claude', 'five_hour',  {recent}, 77, 300, NULL, 100, NULL, NULL, NULL);
            """);
    }
}
