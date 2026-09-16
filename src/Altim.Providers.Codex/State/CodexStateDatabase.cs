using Altim.Providers.Io;
using Microsoft.Data.Sqlite;

namespace Altim.Providers.Codex.State;

/// <summary>
/// Reads the Codex state database to find which sessions were active recently.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the offline fallback affordable. Candidate rollout files come from
/// <c>threads</c> ordered by last activity, so the reader opens the newest handful of files
/// instead of walking a session tree that measured 28.3 GB across 2,518 files.
/// </para>
/// <para>
/// The database is opened <b>read-only</b>, with pooling off so no handle outlives the
/// call. It belongs to another application that is very likely writing to it right now; a
/// locked or busy database yields nothing and the caller falls back to modification time.
/// </para>
/// <para>
/// Columns are discovered rather than assumed. These filenames carry schema versions —
/// <c>state_5</c>, and the number has changed before — so a query naming a column that a
/// future schema dropped would turn a working fallback into a hard failure. Only columns
/// the table actually has are selected.
/// </para>
/// <para>
/// <c>logs_2.sqlite</c> is not read. It looks like a usage source, is 604 MB, and was stale
/// by days on the verification machine.
/// </para>
/// </remarks>
public sealed class CodexStateDatabase
{
    private const string ThreadsTable = "threads";

    private static readonly string[] IdColumns = ["id", "thread_id", "uuid"];
    private static readonly string[] ModelColumns = ["model", "model_id", "model_slug"];
    private static readonly string[] TokenColumns = ["tokens_used", "token_count", "total_tokens"];
    private static readonly string[] UpdatedColumns = ["updated_at_ms", "updated_at", "last_activity_ms"];
    private static readonly string[] CreatedColumns = ["created_at_ms", "created_at", "started_at_ms"];
    private static readonly string[] RolloutColumns = ["rollout_path", "path", "rollout"];

    private readonly string _databasePath;

    /// <summary>
    /// Creates a reader over a specific database file.
    /// </summary>
    /// <param name="databasePath">The state database.</param>
    public CodexStateDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _databasePath = databasePath;
    }

    /// <summary>
    /// Reads the most recently active threads.
    /// </summary>
    /// <param name="limit">The most rows to return.</param>
    /// <returns>
    /// Newest first. Empty when the database is missing, locked, or has no
    /// <c>threads</c> table, none of which is an error.
    /// </returns>
    public IReadOnlyList<CodexThreadSummary> ReadRecentThreads(int limit)
    {
        return ReadRecent(limit).Select(static row => row.Summary).ToList();
    }

    /// <summary>
    /// Reads the most recently active threads together with the rollout file each one
    /// wrote, for the caller to tail.
    /// </summary>
    /// <param name="limit">The most rows to return.</param>
    /// <returns>Newest first. Empty when the database cannot be read.</returns>
    internal IReadOnlyList<CodexThreadRow> ReadRecent(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (!File.Exists(_databasePath))
        {
            return [];
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            HashSet<string> columns = ReadColumns(connection);
            if (columns.Count == 0)
            {
                return [];
            }

            string? idColumn = Pick(columns, IdColumns);
            string? modelColumn = Pick(columns, ModelColumns);
            string? tokensColumn = Pick(columns, TokenColumns);
            string? updatedColumn = Pick(columns, UpdatedColumns);
            string? createdColumn = Pick(columns, CreatedColumns);
            string? rolloutColumn = Pick(columns, RolloutColumns);

            var selected = new List<string>();
            AddIfPresent(selected, idColumn);
            AddIfPresent(selected, modelColumn);
            AddIfPresent(selected, tokensColumn);
            AddIfPresent(selected, updatedColumn);
            AddIfPresent(selected, createdColumn);
            AddIfPresent(selected, rolloutColumn);

            if (selected.Count == 0)
            {
                return [];
            }

            string order = updatedColumn is null
                ? string.Empty
                : " ORDER BY " + Quote(updatedColumn) + " DESC";

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT " + string.Join(", ", selected.Select(Quote))
                + " FROM " + Quote(ThreadsTable)
                + order
                + " LIMIT $limit";
            _ = command.Parameters.AddWithValue("$limit", limit);

            var rows = new List<CodexThreadRow>(limit);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadRow(reader, idColumn, modelColumn, tokensColumn, updatedColumn, createdColumn, rolloutColumn));
            }

            return rows;
        }
        catch (SqliteException)
        {
            // Busy, locked, corrupt, or a schema that moved on. The caller falls back.
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    private static void AddIfPresent(List<string> selected, string? column)
    {
        if (column is not null)
        {
            selected.Add(column);
        }
    }

    private static CodexThreadRow ReadRow(
        SqliteDataReader reader,
        string? idColumn,
        string? modelColumn,
        string? tokensColumn,
        string? updatedColumn,
        string? createdColumn,
        string? rolloutColumn)
    {
        string? id = idColumn is null ? null : ReadIdentifier(reader, idColumn);
        string? model = modelColumn is null ? null : ReadIdentifier(reader, modelColumn);
        long? tokens = tokensColumn is null ? null : ReadCount(reader, tokensColumn);
        DateTimeOffset? updated = updatedColumn is null ? null : ReadInstant(reader, updatedColumn);
        DateTimeOffset? created = createdColumn is null ? null : ReadInstant(reader, createdColumn);
        string? rollout = rolloutColumn is null ? null : ReadRawPath(reader, rolloutColumn);

        return new CodexThreadRow(new CodexThreadSummary(id, model, tokens, updated, created), rollout);
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        _ = command.Parameters.AddWithValue("$table", ThreadsTable);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                _ = columns.Add(reader.GetString(0));
            }
        }

        return columns;
    }

    private static string? Pick(HashSet<string> columns, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (columns.TryGetValue(candidate, out string? actual))
            {
                return actual;
            }
        }

        return null;
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string? ReadIdentifier(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string? value = reader.GetFieldType(ordinal) == typeof(string)
            ? reader.GetString(ordinal)
            : reader.GetValue(ordinal)?.ToString();

        return JsonValues.IsIdentifier(value) ? value : null;
    }

    private static long? ReadCount(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            long value = reader.GetInt64(ordinal);
            return value >= 0 ? value : null;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadInstant(SqliteDataReader reader, string column)
    {
        long? raw = ReadCount(reader, column);
        if (raw is not { } value || value <= 0)
        {
            return null;
        }

        // Columns named "_ms" hold milliseconds, but the schema is versioned and the units
        // have moved before. Decide by magnitude instead: anything past the year 2100 in
        // seconds is milliseconds.
        const long MaxPlausibleSeconds = 4_102_444_800L;
        try
        {
            return value > MaxPlausibleSeconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadRawPath(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            string value = reader.GetString(ordinal);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }
}

/// <summary>
/// A thread row together with the rollout file it wrote.
/// </summary>
/// <param name="Summary">The parts of the row that may leave this assembly.</param>
/// <param name="RolloutPath">
/// The rollout file, used only to decide which few files to tail. Internal on purpose: it
/// is a filesystem path and nothing outside the Codex reader has any business holding one.
/// </param>
internal sealed record CodexThreadRow(CodexThreadSummary Summary, string? RolloutPath);
