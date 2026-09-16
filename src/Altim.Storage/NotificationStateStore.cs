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
/// The primary key is (provider, metric, threshold), so recording the same threshold
/// again updates the row in place rather than growing the table. Nothing here records
/// what the user was doing when a notification fired, only that it did.
/// </remarks>
public sealed class NotificationStateStore
{
    private const string Columns = "provider_id, metric_key, threshold, fired_at, window_resets_at";

    private readonly AltimDatabase _database;

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
            WHERE provider_id = $provider AND metric_key = $metric
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
    public async ValueTask ReplaceAllAsync(IReadOnlyList<NotificationState> fired,
                                            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fired);

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
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
            states.Add(new NotificationState(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))));
        }

        return states;
    }
}
