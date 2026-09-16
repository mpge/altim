using System.Data.Common;
using Altim.Core.Notifications;
using Microsoft.Data.Sqlite;

namespace Altim.Storage;

/// <summary>
/// The <c>notification_state</c> table: which thresholds have already fired, so the
/// evaluator in <c>Altim.Core</c> can stay a pure function over usage, settings and
/// this state.
/// </summary>
/// <remarks>
/// <para>
/// The primary key is (provider, metric, threshold), so recording the same threshold
/// again updates the row in place rather than growing the table. Nothing here records
/// what the user was doing when a notification fired, only that it did.
/// </para>
/// <para>
/// Reading never throws over content, the same way the settings store does not. A row
/// whose threshold is not a whole percentage — hand-edited, or written by something
/// that is not Altim — is ignored rather than allowed to stop notifications working at
/// all. The row is left in the file, where clearing still removes it.
/// </para>
/// </remarks>
public sealed class NotificationStateStore
{
    private const string Columns = "provider_id, metric_key, threshold, fired_at, window_resets_at";

    /// <summary>
    /// The rows a reading will trust. SQLite columns are dynamically typed, so the
    /// declared INTEGER is a preference and not a guarantee; <c>typeof</c> is.
    /// </summary>
    private const string Usable =
        "typeof(threshold) = 'integer' AND threshold BETWEEN 1 AND 100 "
        + "AND typeof(fired_at) = 'integer'";

    /// <summary>The Unix seconds <see cref="DateTimeOffset"/> can represent.</summary>
    private static readonly long MinInstantSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();

    /// <inheritdoc cref="MinInstantSeconds" />
    private static readonly long MaxInstantSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private readonly AltimDatabase _database;

    /// <summary>
    /// What this process last wrote into the table, or null when it has not written yet.
    /// </summary>
    /// <remarks>
    /// Altim is single-instance and is the only writer of its own database, so what it wrote
    /// last is what the table holds. The snapshot is deliberately not seeded from a read: a
    /// row that is on file but unusable — a hand-edited threshold that is not a percentage —
    /// is filtered out of every read, so a snapshot taken from one would make the first
    /// replacement skip the write that removes it.
    /// </remarks>
    private volatile IReadOnlyList<NotificationState>? _written;

    /// <summary>
    /// Creates the store over an open database.
    /// </summary>
    /// <param name="database">The open database.</param>
    public NotificationStateStore(AltimDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Records that a threshold fired, replacing any earlier record of the same
    /// threshold for the same metric.
    /// </summary>
    /// <param name="state">What fired, and for which window.</param>
    /// <param name="ct">Cancels the write.</param>
    public async ValueTask RecordAsync(NotificationState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        _written = null;

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        await InsertAsync(lease.Connection, state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every recorded firing, for handing to the evaluator in one go.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>All rows, ordered by provider, metric and threshold.</returns>
    public async ValueTask<IReadOnlyList<NotificationState>> GetAllAsync(CancellationToken ct)
    {
        using SqliteConnection connection = _database.OpenRead();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM notification_state
            WHERE {Usable}
            ORDER BY provider_id, metric_key, threshold
            """;

        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every recorded firing for one metric.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="metricKey">The metric.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The rows, ordered by threshold.</returns>
    public async ValueTask<IReadOnlyList<NotificationState>> GetAsync(
        string providerId, string metricKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        ArgumentException.ThrowIfNullOrEmpty(metricKey);

        using SqliteConnection connection = _database.OpenRead();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM notification_state
            WHERE provider_id = $provider AND metric_key = $metric AND {Usable}
            ORDER BY threshold
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$metric", metricKey);

        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets every threshold recorded for one metric. This is what runs when a limit
    /// window rolls over and the next window is allowed to notify again.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="metricKey">The metric.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>How many rows were forgotten.</returns>
    public async ValueTask<int> ClearMetricAsync(string providerId, string metricKey,
                                                  CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        ArgumentException.ThrowIfNullOrEmpty(metricKey);

        _written = null;

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);

        using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText = """
            DELETE FROM notification_state
            WHERE provider_id = $provider AND metric_key = $metric
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$metric", metricKey);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets every threshold whose window has already rolled over.
    /// </summary>
    /// <param name="now">The instant to judge a window by.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>How many rows were forgotten.</returns>
    /// <remarks>
    /// A row with no reset instant is left alone. Altim does not invent a reset time, so
    /// it cannot know that one has passed.
    /// </remarks>
    public async ValueTask<int> ClearExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        _written = null;

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);

        using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText = """
            DELETE FROM notification_state
            WHERE window_resets_at IS NOT NULL AND window_resets_at <= $now
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets everything. Notifications may fire again from a clean slate.
    /// </summary>
    /// <param name="ct">Cancels the write.</param>
    public async ValueTask ClearAllAsync(CancellationToken ct)
    {
        _written = null;

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);

        using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText = "DELETE FROM notification_state";

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the persisted firings as the state the evaluator resumes from.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// A <see cref="ThresholdState"/> built with <see cref="ThresholdState.FromPersisted"/>,
    /// so the first evaluation after a restart stays silent.
    /// </returns>
    public async ValueTask<ThresholdState> LoadThresholdStateAsync(CancellationToken ct)
        => ThresholdState.FromPersisted(await GetAllAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Replaces the whole table with the firings an evaluation produced, in one
    /// transaction. This is the counterpart of <see cref="LoadThresholdStateAsync"/>: the
    /// evaluator returns a complete next state, so persisting it is a replacement rather
    /// than a set of edits, and an entry the evaluator dropped is gone from the file too.
    /// </summary>
    /// <param name="fired">The entries to keep. An empty list empties the table.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>
    /// True when the table was rewritten, false when it already held exactly this and
    /// nothing was written at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>An unchanged state writes nothing, and that is not a micro-optimisation.</b> The
    /// evaluator returns a complete next state on every reading, and almost every reading
    /// leaves it identical: nothing has crossed a threshold and no window has rolled over.
    /// Rewriting it anyway is a delete plus an insert per row inside a write transaction,
    /// several times a minute while an agent works, and every one of those commits is
    /// write-ahead log the checkpoint then has to carry — for a table that has not changed
    /// since the last time it was written.
    /// </para>
    /// <para>
    /// The comparison is against what this process last wrote rather than against a read of
    /// the table, so the skip costs nothing at all: no connection, no writer lease, and no
    /// transaction.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> ReplaceAllAsync(IReadOnlyList<NotificationState> fired,
                                                  CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fired);

        if (_written is { } known && Same(known, fired))
        {
            return false;
        }

        using (WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false))
        {
            SqliteConnection connection = lease.Connection;

            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand clear = connection.CreateCommand())
            {
                clear.CommandText = "DELETE FROM notification_state";
                _ = await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (NotificationState state in fired)
            {
                await InsertAsync(connection, state, ct).ConfigureAwait(false);
            }

            transaction.Commit();
        }

        // Recorded only after the commit: a write that threw leaves the snapshot unset, and
        // the next call writes rather than trusting a state that may not be on file.
        _written = [.. fired];
        return true;
    }

    /// <summary>Whether two states hold the same entries in the same order.</summary>
    /// <param name="left">What was written last.</param>
    /// <param name="right">What the evaluator has just produced.</param>
    private static bool Same(IReadOnlyList<NotificationState> left, IReadOnlyList<NotificationState> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static async ValueTask InsertAsync(SqliteConnection connection, NotificationState state,
                                                CancellationToken ct)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO notification_state ({Columns})
            VALUES ($provider, $metric, $threshold, $fired, $resets)
            ON CONFLICT (provider_id, metric_key, threshold) DO UPDATE
            SET fired_at = excluded.fired_at, window_resets_at = excluded.window_resets_at
            """;
        command.Parameters.AddWithValue("$provider", state.ProviderId);
        command.Parameters.AddWithValue("$metric", state.MetricKey);
        command.Parameters.AddWithValue("$threshold", state.Threshold);
        command.Parameters.AddWithValue("$fired", state.FiredAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue(
            "$resets", (object?)state.WindowResetsAt?.ToUnixTimeSeconds() ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<NotificationState>> ReadAllAsync(
        SqliteCommand command, CancellationToken ct)
    {
        List<NotificationState> states = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // fired_at is already known to be an integer; it can still be an integer no
            // instant exists for, and a row Altim cannot date is a row it cannot reason
            // about, so it is passed over rather than guessed at.
            if (Instant(reader, 3) is not { } firedAt)
            {
                continue;
            }

            states.Add(new NotificationState(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                firedAt,
                Instant(reader, 4)));
        }

        return states;
    }

    private static DateTimeOffset? Instant(DbDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not long seconds
            || seconds < MinInstantSeconds || seconds > MaxInstantSeconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
