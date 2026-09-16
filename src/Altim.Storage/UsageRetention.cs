using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Altim.Storage;

/// <summary>
/// Keeps the history file small: rows older than the retention window are collapsed to
/// one row an hour, and the file is compacted occasionally.
/// </summary>
/// <remarks>
/// <para>
/// Down-sampling is a three-statement transaction. A temporary table holds the
/// aggregate for every hour bucket that actually needs collapsing; the original rows in
/// exactly those buckets are deleted; the aggregates are inserted in their place. A
/// bucket that already holds one row sitting on the hour is not in the temporary table,
/// which is what makes a second run a genuine no-op rather than a churn of identical
/// rows.
/// </para>
/// <para>
/// What survives the collapse is chosen per column, because the columns mean different
/// things. The percentage kept is the <em>maximum</em>, because the point of old history
/// is how close to the limit the user came. Everything else — the token counters, the
/// window length and the reset instant — is taken from the <em>last</em> row in the
/// bucket, the one with the greatest <c>captured_at</c>. Token columns hold a provider's
/// running total, not the work done since the previous row, so summing them multiplies
/// the same total by however many times it was observed; taking the last row reads a
/// counter as its end-of-hour value, survives a counter that resets mid-hour, and keeps
/// the reset instant attached to the window it was actually reported with.
/// </para>
/// <para>
/// Both operations here rewrite large parts of the file, so both run on a worker rather
/// than on the thread that asks for them.
/// </para>
/// </remarks>
public sealed class UsageRetention
{
    /// <summary>
    /// How long full-resolution history is kept before an hour of it becomes one row.
    /// </summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// How long between compactions. Rarely, because VACUUM rewrites the whole file.
    /// </summary>
    public static readonly TimeSpan DefaultVacuumInterval = TimeSpan.FromDays(30);

    /// <summary>
    /// The setting key holding the last compaction instant, as Unix seconds. It is a
    /// timestamp and nothing else; no path or identity is recorded with it.
    /// </summary>
    public const string LastVacuumKey = "maintenance.last_vacuum_at";

    private const int BucketSeconds = 3600;
    private const string TempTable = "temp.altim_downsample";

    private readonly AltimDatabase _database;

    /// <summary>
    /// Creates the maintenance operations over an open database.
    /// </summary>
    /// <param name="database">The open database.</param>
    public UsageRetention(AltimDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Collapses history older than <see cref="DefaultRetention"/> to hourly rows.
    /// </summary>
    /// <param name="now">The instant to measure the retention window back from.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>What the run changed.</returns>
    public ValueTask<RetentionResult> DownsampleAsync(DateTimeOffset now, CancellationToken ct)
        => DownsampleAsync(now, DefaultRetention, ct);

    /// <summary>
    /// Collapses history older than an explicit retention window to hourly rows. Safe to
    /// run as often as anything likes: a run with nothing left to collapse touches no
    /// rows.
    /// </summary>
    /// <param name="now">The instant to measure the retention window back from.</param>
    /// <param name="retention">How much full-resolution history to keep.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>What the run changed.</returns>
    /// <remarks>
    /// Real work, on a worker: this reads, deletes and rewrites every row older than the
    /// retention window.
    /// </remarks>
    public async ValueTask<RetentionResult> DownsampleAsync(DateTimeOffset now, TimeSpan retention,
                                                            CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero);

        long cutoff = now.Subtract(retention).ToUnixTimeSeconds();

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        return await Task.Run(() => Downsample(connection, cutoff, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compacts the file if it has not been compacted for <paramref name="minimumInterval"/>.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="minimumInterval">The shortest gap between two compactions.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>
    /// True when the file was compacted. The very first call on a database never
    /// compacts: there is nothing to reclaim yet, so it only starts the clock.
    /// </returns>
    /// <remarks>
    /// Real work, on a worker, whenever it decides a compaction is due: VACUUM rewrites
    /// the whole file.
    /// </remarks>
    public async ValueTask<bool> VacuumIfDueAsync(DateTimeOffset now, TimeSpan minimumInterval,
                                                   CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumInterval, TimeSpan.Zero);

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        return await Task.Run(() => VacuumIfDue(connection, now, minimumInterval, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Compacts the file now, whatever the schedule says. Offered for the moment after
    /// the user clears their history, when the reclaimed space is the point.
    /// </summary>
    /// <param name="ct">Cancels the work.</param>
    /// <remarks>
    /// Real work, on a worker: VACUUM rewrites the whole file.
    /// </remarks>
    public async ValueTask VacuumAsync(CancellationToken ct)
    {
        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        await Task.Run(() => Vacuum(connection), ct).ConfigureAwait(false);
    }

    private static RetentionResult Downsample(SqliteConnection connection, long cutoff,
                                              CancellationToken ct)
    {
        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            BuildBuckets(connection, cutoff);
            ct.ThrowIfCancellationRequested();

            int collapsed = DeleteCollapsed(connection, cutoff);
            ct.ThrowIfCancellationRequested();

            int written = InsertBuckets(connection);

            transaction.Commit();

            return new RetentionResult(collapsed, written);
        }
        finally
        {
            DropBuckets(connection);
        }
    }

    private static bool VacuumIfDue(SqliteConnection connection, DateTimeOffset now,
                                    TimeSpan minimumInterval, CancellationToken ct)
    {
        string? recorded = SettingTable.Read(connection, LastVacuumKey);
        long nowSeconds = now.ToUnixTimeSeconds();

        // No usable marker: a database nobody has compacted yet has nothing to reclaim,
        // so this call only starts the clock. A marker edited into nonsense is treated
        // the same way rather than triggering a rewrite of the whole file, and so is a
        // marker dated in the future — a clock that was wrong once must not defer
        // compaction until it is right again.
        if (recorded is null
            || !long.TryParse(recorded, NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out long last)
            || last > nowSeconds)
        {
            RecordVacuum(connection, nowSeconds);
            return false;
        }

        if (nowSeconds - last < (long)minimumInterval.TotalSeconds)
        {
            // Not due. The marker is left alone, so waiting does not push the next
            // compaction further away every time something asks.
            return false;
        }

        ct.ThrowIfCancellationRequested();

        Vacuum(connection);
        RecordVacuum(connection, nowSeconds);
        return true;
    }

    private static void RecordVacuum(SqliteConnection connection, long nowSeconds)
        => SettingTable.Write(connection, LastVacuumKey,
                              nowSeconds.ToString(CultureInfo.InvariantCulture));

    private static void Vacuum(SqliteConnection connection)
    {
        // VACUUM cannot run inside a transaction, so it runs on the bare connection.
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "VACUUM";
        _ = command.ExecuteNonQuery();
    }

    private static void BuildBuckets(SqliteConnection connection, long cutoff)
    {
        DropBuckets(connection);

        using SqliteCommand command = connection.CreateCommand();

        // Two passes over the same rows: "summary" decides which buckets need collapsing
        // and carries the peak percentage, "ending" picks each bucket's last row. They
        // are joined rather than merged into one aggregate because SQLite's bare-column
        // rule only picks a row for you when the query has exactly one min() or max(),
        // and this one has a max() that must come from a different row.
        command.CommandText = $"""
            CREATE TEMP TABLE altim_downsample AS
            WITH bucketed AS (
                SELECT id,
                       provider_id,
                       metric_key,
                       captured_at,
                       (captured_at / {BucketSeconds}) * {BucketSeconds} AS bucket,
                       used_percent,
                       window_minutes,
                       resets_at,
                       input_tokens,
                       output_tokens,
                       cache_read_tokens,
                       cache_write_tokens
                FROM usage_sample
                WHERE captured_at < $cutoff
            ),
            summary AS (
                SELECT provider_id,
                       metric_key,
                       bucket,
                       MAX(used_percent) AS used_percent
                FROM bucketed
                GROUP BY provider_id, metric_key, bucket
                HAVING COUNT(*) > 1 OR MIN(captured_at) % {BucketSeconds} <> 0
            ),
            ending AS (
                SELECT provider_id,
                       metric_key,
                       bucket,
                       window_minutes,
                       resets_at,
                       input_tokens,
                       output_tokens,
                       cache_read_tokens,
                       cache_write_tokens,
                       ROW_NUMBER() OVER (PARTITION BY provider_id, metric_key, bucket
                                          ORDER BY captured_at DESC, id DESC) AS recency
                FROM bucketed
            )
            SELECT summary.provider_id       AS provider_id,
                   summary.metric_key        AS metric_key,
                   summary.bucket            AS bucket,
                   summary.used_percent      AS used_percent,
                   ending.window_minutes     AS window_minutes,
                   ending.resets_at          AS resets_at,
                   ending.input_tokens       AS input_tokens,
                   ending.output_tokens      AS output_tokens,
                   ending.cache_read_tokens  AS cache_read_tokens,
                   ending.cache_write_tokens AS cache_write_tokens
            FROM summary
            JOIN ending
              ON ending.provider_id = summary.provider_id
             AND ending.metric_key = summary.metric_key
             AND ending.bucket = summary.bucket
             AND ending.recency = 1
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);

        _ = command.ExecuteNonQuery();
    }

    private static int DeleteCollapsed(SqliteConnection connection, long cutoff)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM usage_sample
            WHERE captured_at < $cutoff
              AND EXISTS (
                  SELECT 1 FROM {TempTable} AS bucketed
                  WHERE bucketed.provider_id = usage_sample.provider_id
                    AND bucketed.metric_key = usage_sample.metric_key
                    AND bucketed.bucket = (usage_sample.captured_at / {BucketSeconds}) * {BucketSeconds})
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);

        return command.ExecuteNonQuery();
    }

    private static int InsertBuckets(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO usage_sample (provider_id, metric_key, captured_at, used_percent,
                                      window_minutes, resets_at, input_tokens, output_tokens,
                                      cache_read_tokens, cache_write_tokens)
            SELECT provider_id, metric_key, bucket, used_percent,
                   window_minutes, resets_at, input_tokens, output_tokens,
                   cache_read_tokens, cache_write_tokens
            FROM {TempTable}
            """;

        return command.ExecuteNonQuery();
    }

    private static void DropBuckets(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {TempTable}";
        _ = command.ExecuteNonQuery();
    }
}
