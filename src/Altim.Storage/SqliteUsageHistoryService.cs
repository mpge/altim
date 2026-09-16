using System.Data.Common;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Microsoft.Data.Sqlite;

namespace Altim.Storage;

/// <summary>
/// The durable usage history, in the <c>usage_sample</c> table.
/// </summary>
/// <remarks>
/// <para>
/// A reading is stored only when it differs from the last row for that provider and
/// metric, so an idle machine adds nothing to the file for as long as it stays idle.
/// That is what keeps the database inside its size budget without a background job.
/// It also means an empty range is not an empty history: see
/// <see cref="GetLatestBeforeAsync"/>.
/// </para>
/// <para>
/// Unknown stays unknown: a metric the provider did not report, or reported
/// implausibly, is written as NULL and read back as <see langword="null"/>, never as
/// zero. Nothing but numbers, timestamps and the provider and metric identifiers is
/// written; no project name, prompt, command or path reaches this layer at all.
/// </para>
/// <para>
/// Microsoft.Data.Sqlite runs every statement inline, so an <c>async</c> signature here
/// buys nothing by itself. The two read methods and their callers can be looking at
/// thirty days of rows, so they are moved onto a worker; <see cref="RecordAsync"/> is a
/// handful of single-row statements and stays on the calling thread.
/// </para>
/// </remarks>
public sealed class SqliteUsageHistoryService : IUsageHistoryService
{
    private const string SampleColumns =
        "provider_id, metric_key, captured_at, used_percent, window_minutes, resets_at, "
        + "input_tokens, output_tokens, cache_read_tokens, cache_write_tokens";

    private readonly AltimDatabase _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the service over an open database, timed by the system clock.
    /// </summary>
    /// <param name="database">The open database.</param>
    public SqliteUsageHistoryService(AltimDatabase database)
        : this(database, TimeProvider.System)
    {
    }

    /// <summary>
    /// Creates the service over an open database with an explicit clock, which is how
    /// the tests drive it.
    /// </summary>
    /// <param name="database">The open database.</param>
    /// <param name="timeProvider">
    /// Supplies the capture instant for a reading that does not carry one of its own.
    /// </param>
    public SqliteUsageHistoryService(AltimDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _database = database;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A reading in <see cref="ProviderStatus.Error"/> is not recorded at all. Its
    /// metrics are unavailable rather than zero or null-because-measured, and writing
    /// them would turn a transient read failure into a hole in the history that later
    /// reads cannot tell apart from a genuine drop to nothing. This matches what
    /// <c>UsageAggregator</c> and <c>ThresholdEvaluator</c> do with the same reading.
    /// </para>
    /// <para>
    /// Runs on the calling thread: one short lookup and at most one insert per metric,
    /// inside one transaction. It is called from the monitoring scheduler, which is
    /// already off the UI thread.
    /// </para>
    /// </remarks>
    public async ValueTask RecordAsync(ProviderUsage usage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (usage.Status == ProviderStatus.Error || usage.Metrics.Count == 0)
        {
            return;
        }

        long capturedAt = (usage.LastRefreshed ?? _timeProvider.GetUtcNow()).ToUnixTimeSeconds();

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (UsageMetric metric in usage.Metrics)
        {
            // Known and deliberately not fixed yet: the token totals belong to the
            // reading, not to the metric, so an hour in which one metric moves writes the
            // same token counts onto every metric's row. Revisit once real growth has
            // been measured against the 5MB/year budget; splitting them into their own
            // table is a migration, not a tidy-up.
            SampleValues candidate = ToValues(metric, usage.Tokens);
            SampleValues? previous = await ReadLatestAsync(connection, usage.ProviderId, metric.Key, ct)
                .ConfigureAwait(false);

            // A value that has not moved is not history, it is noise. Float comparison is
            // exact on purpose: both sides came from the same provider reading and went
            // through SQLite's REAL column, which round-trips an IEEE754 double unchanged.
            if (previous == candidate)
            {
                continue;
            }

            await InsertAsync(connection, usage.ProviderId, metric.Key, capturedAt, candidate, ct)
                .ConfigureAwait(false);
        }

        transaction.Commit();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on a worker: a 30-day range is the case this exists for, and the dashboard
    /// asks for one from the UI thread.
    /// </remarks>
    public async ValueTask<IReadOnlyList<UsageSample>> GetRangeAsync(
        string providerId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);

        long fromSeconds = from.ToUnixTimeSeconds();
        long toSeconds = to.ToUnixTimeSeconds();

        return await Task.Run(() => ReadRange(providerId, fromSeconds, toSeconds, ct), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on a worker. The lookup is one row per metric, but it reaches back past the
    /// retention boundary on a file that may hold a year of history.
    /// </remarks>
    public async ValueTask<IReadOnlyList<UsageSample>> GetLatestBeforeAsync(
        string providerId, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);

        long atSeconds = at.ToUnixTimeSeconds();

        return await Task.Run(() => ReadLatestBefore(providerId, atSeconds, ct), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on the calling thread: one statement, behind the write gate. Reclaiming the
    /// space it frees is <see cref="UsageRetention.VacuumAsync"/>, which does not.
    /// </remarks>
    public async ValueTask ClearAsync(CancellationToken ct)
    {
        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);

        using SqliteCommand command = lease.Connection.CreateCommand();

        // History only. Settings and notification state are not history and survive this.
        command.CommandText = "DELETE FROM usage_sample";
        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private IReadOnlyList<UsageSample> ReadRange(string providerId, long from, long to,
                                                 CancellationToken ct)
    {
        using SqliteConnection connection = _database.OpenRead();
        using SqliteCommand command = connection.CreateCommand();

        // Half open: the lower bound is included, the upper bound is not, so adjacent
        // ranges neither overlap nor drop a sample between them.
        command.CommandText = $"""
            SELECT {SampleColumns}
            FROM usage_sample
            WHERE provider_id = $provider AND captured_at >= $from AND captured_at < $to
            ORDER BY captured_at, id
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        return ReadSamples(command, ct);
    }

    private IReadOnlyList<UsageSample> ReadLatestBefore(string providerId, long at,
                                                        CancellationToken ct)
    {
        using SqliteConnection connection = _database.OpenRead();
        using SqliteCommand command = connection.CreateCommand();

        // Strictly before, and one row per metric: the newest row wins, with the larger
        // id breaking a tie between two rows stamped the same second, which is the same
        // order the "has this moved?" lookup uses.
        command.CommandText = $"""
            SELECT {SampleColumns}
            FROM (
                SELECT {SampleColumns},
                       ROW_NUMBER() OVER (PARTITION BY metric_key
                                          ORDER BY captured_at DESC, id DESC) AS recency
                FROM usage_sample
                WHERE provider_id = $provider AND captured_at < $at
            )
            WHERE recency = 1
            ORDER BY metric_key
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$at", at);

        return ReadSamples(command, ct);
    }

    private static List<UsageSample> ReadSamples(SqliteCommand command, CancellationToken ct)
    {
        List<UsageSample> samples = [];

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            samples.Add(ReadSample(reader));
        }

        return samples;
    }

    private static SampleValues ToValues(UsageMetric metric, TokenTotals? tokens)
    {
        // ReportedPercent, not the raw value: a provider defect can report 101, and history
        // must never store a figure the UI would refuse to render.
        double? usedPercent = metric.ReportedPercent;
        long? windowMinutes = metric.Window is null
            ? null
            : (long)Math.Round(metric.Window.Length.TotalMinutes, MidpointRounding.AwayFromZero);

        return new SampleValues(
            usedPercent,
            windowMinutes,
            metric.Window?.ResetsAt?.ToUnixTimeSeconds(),
            tokens?.Input,
            tokens?.Output,
            tokens?.CacheRead,
            tokens?.CacheWrite);
    }

    private static async ValueTask<SampleValues?> ReadLatestAsync(
        SqliteConnection connection, string providerId, string metricKey, CancellationToken ct)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT used_percent, window_minutes, resets_at,
                   input_tokens, output_tokens, cache_read_tokens, cache_write_tokens
            FROM usage_sample
            WHERE provider_id = $provider AND metric_key = $metric
            ORDER BY captured_at DESC, id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$metric", metricKey);

        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SampleValues(
            NullableDouble(reader, 0),
            NullableInt64(reader, 1),
            NullableInt64(reader, 2),
            NullableInt64(reader, 3),
            NullableInt64(reader, 4),
            NullableInt64(reader, 5),
            NullableInt64(reader, 6));
    }

    private static async ValueTask InsertAsync(SqliteConnection connection, string providerId,
                                                string metricKey, long capturedAt,
                                                SampleValues values, CancellationToken ct)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO usage_sample ({SampleColumns})
            VALUES ($provider, $metric, $captured, $percent, $window, $resets,
                    $input, $output, $cacheRead, $cacheWrite)
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$metric", metricKey);
        command.Parameters.AddWithValue("$captured", capturedAt);
        AddNullable(command, "$percent", values.UsedPercent);
        AddNullable(command, "$window", values.WindowMinutes);
        AddNullable(command, "$resets", values.ResetsAt);
        AddNullable(command, "$input", values.InputTokens);
        AddNullable(command, "$output", values.OutputTokens);
        AddNullable(command, "$cacheRead", values.CacheReadTokens);
        AddNullable(command, "$cacheWrite", values.CacheWriteTokens);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static UsageSample ReadSample(DbDataReader reader)
    {
        long? windowMinutes = NullableInt64(reader, 4);
        long? resetsAt = NullableInt64(reader, 5);

        long? input = NullableInt64(reader, 6);
        long? output = NullableInt64(reader, 7);
        long? cacheRead = NullableInt64(reader, 8);
        long? cacheWrite = NullableInt64(reader, 9);

        TokenTotals? tokens = input is null && output is null && cacheRead is null && cacheWrite is null
            ? null
            : new TokenTotals(input, output, cacheRead, cacheWrite);

        return new UsageSample(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
            NullableDouble(reader, 3),
            windowMinutes is null ? null : TimeSpan.FromMinutes(windowMinutes.Value),
            resetsAt is null ? null : DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value),
            tokens);
    }

    private static void AddNullable(SqliteCommand command, string name, double? value)
        => command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private static void AddNullable(SqliteCommand command, string name, long? value)
        => command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private static double? NullableDouble(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static long? NullableInt64(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    /// <summary>
    /// Everything about a sample except which provider, metric and instant it belongs
    /// to. Comparing two of these is the whole of the "has anything changed?" decision,
    /// and a record struct gives that comparison for free.
    /// </summary>
    private readonly record struct SampleValues(
        double? UsedPercent,
        long? WindowMinutes,
        long? ResetsAt,
        long? InputTokens,
        long? OutputTokens,
        long? CacheReadTokens,
        long? CacheWriteTokens);
}
