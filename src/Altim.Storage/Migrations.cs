using Microsoft.Data.Sqlite;

namespace Altim.Storage;

/// <summary>
/// The forward-only migration ladder. Each rung is a version number and the SQL that
/// takes the database from the rung below it to that version; each runs inside its own
/// transaction and records its version in <c>schema_version</c> as part of that same
/// transaction, so an interrupted upgrade leaves the file at the last version that
/// completed rather than somewhere between two.
/// </summary>
/// <remarks>
/// <para>
/// Adding a migration is one line: append <c>new Migration(4, "...")</c> to
/// <see cref="Ladder"/> and raise <see cref="SqliteSchema.CurrentVersion"/> to match.
/// Nothing else changes, and no rung already on the ladder is ever edited — not
/// <see cref="SqliteSchema.CreateScript"/> and not the ones after it — because an existing
/// database only ever sees the new rung and an empty file climbs every rung in turn. A rung
/// that is changed after it has shipped runs on neither.
/// </para>
/// <para>
/// Two instances starting at once is the normal case, not an exotic one: autostart
/// launches Altim at login and the user may launch it again by hand a moment later. So
/// each rung opens an immediate transaction and re-reads <c>schema_version</c> inside
/// it. The instance that gets there second finds the rung already applied and skips it,
/// rather than running the same DDL twice and failing with "table already exists".
/// </para>
/// <para>
/// There is no downgrade path on purpose. A file written by a newer build is rejected
/// with <see cref="AltimSchemaException"/> rather than opened hopefully.
/// </para>
/// </remarks>
public static class Migrations
{
    /// <summary>
    /// One rung of the ladder.
    /// </summary>
    /// <param name="Version">
    /// The version the database is at once <paramref name="Sql"/> has been applied.
    /// Versions are sequential and start at 1.
    /// </param>
    /// <param name="Sql">
    /// The statements to run. They may create, alter or backfill; they may not drop a
    /// column that an older build still writes to.
    /// </param>
    internal sealed record Migration(int Version, string Sql);

    /// <summary>
    /// Every migration, oldest first. This ordering is the schema's history and is
    /// append only.
    /// </summary>
    private static readonly Migration[] Ordered =
    [
        new Migration(1, SqliteSchema.CreateScript),
        new Migration(2, SqliteSchema.CreateUsageDayScript),
        new Migration(3, SqliteSchema.ClearObservedDayTokensScript),
    ];

    /// <summary>
    /// The newest version this build can bring a database to.
    /// </summary>
    public static int LatestVersion => Ordered[^1].Version;

    /// <summary>
    /// The real ladder, exposed to the tests. One rung is not enough to prove that a
    /// ladder climbs in order, stops where it should, and unwinds a rung that fails, so
    /// the tests build ladders of their own and run them through the same code this
    /// property feeds.
    /// </summary>
    internal static IReadOnlyList<Migration> Ladder => Ordered;

    /// <summary>
    /// Brings an open connection's database up to <see cref="LatestVersion"/>.
    /// </summary>
    /// <param name="connection">An open read-write connection.</param>
    /// <returns>The version the database is at afterwards.</returns>
    /// <exception cref="AltimSchemaException">
    /// The database is at a version this build does not know, or its
    /// <c>schema_version</c> table does not hold exactly one usable row.
    /// </exception>
    public static int Apply(SqliteConnection connection) => Apply(connection, Ordered);

    /// <summary>
    /// Reads the version recorded in a database.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <returns>
    /// The recorded version, or 0 when the database has no <c>schema_version</c> table
    /// at all, which is what an empty file looks like.
    /// </returns>
    /// <exception cref="AltimSchemaException">
    /// The table exists but does not hold exactly one row, or the row it holds is not a
    /// positive whole number. A version that cannot be read is never quietly treated as
    /// 0: that would re-run the whole ladder over a file that already has history in it.
    /// </exception>
    public static int ReadVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        exists.Parameters.AddWithValue("$name", "schema_version");

        if (exists.ExecuteScalar() is not long present || present == 0)
        {
            return 0;
        }

        using SqliteCommand read = connection.CreateCommand();

        // typeof() rather than a typed accessor: SQLite columns are dynamically typed, so
        // a hand-edited "one" sits happily in an INTEGER column and reads back through a
        // typed accessor as 0, which is indistinguishable from an empty file.
        read.CommandText = "SELECT typeof(version), version FROM schema_version";

        using SqliteDataReader reader = read.ExecuteReader();
        if (!reader.Read())
        {
            throw new AltimSchemaException(
                "The usage database records no schema version. Altim will not write to a "
                + "database whose shape it cannot confirm.");
        }

        int version = ReadVersionValue(reader);
        if (reader.Read())
        {
            throw new AltimSchemaException(
                "The usage database records more than one schema version. Altim will not "
                + "write to a database whose shape it cannot confirm.");
        }

        return version;
    }

    /// <summary>
    /// Climbs an explicit ladder, which is what <see cref="Apply(SqliteConnection)"/>
    /// does with the real one.
    /// </summary>
    /// <param name="connection">An open read-write connection.</param>
    /// <param name="ladder">The rungs, oldest first.</param>
    /// <param name="beforeRung">
    /// Runs after the version has been read but before a rung's transaction opens. It
    /// exists so a test can let a second connection apply the ladder in exactly that
    /// gap, which is the race two instances starting at once create and which is not
    /// reproducible from outside this method. Null everywhere in production.
    /// </param>
    /// <returns>The version the database is at afterwards.</returns>
    internal static int Apply(SqliteConnection connection, IReadOnlyList<Migration> ladder,
                              Action? beforeRung = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(ladder);

        int latest = ladder.Count == 0 ? 0 : ladder[^1].Version;
        int current = ReadVersion(connection);
        if (current > latest)
        {
            throw AltimSchemaException.FromNewerVersion(current, latest);
        }

        foreach (Migration migration in ladder)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            beforeRung?.Invoke();

            // Immediate rather than deferred: the write lock is taken before the version
            // is read again, so the answer cannot go stale between reading it and acting
            // on it.
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            int applied = ReadVersion(connection);
            if (applied > latest)
            {
                throw AltimSchemaException.FromNewerVersion(applied, latest);
            }

            if (migration.Version <= applied)
            {
                // Another instance climbed this rung while this one waited for the lock.
                transaction.Rollback();
                current = applied;
                continue;
            }

            Execute(connection, migration.Sql);
            RecordVersion(connection, migration.Version);

            transaction.Commit();
            current = migration.Version;
        }

        return current;
    }

    private static int ReadVersionValue(SqliteDataReader reader)
    {
        if (!string.Equals(reader.GetString(0), "integer", StringComparison.Ordinal))
        {
            throw new AltimSchemaException(
                "The usage database records a schema version that is not a whole number. "
                + "Altim will not write to a database whose shape it cannot confirm.");
        }

        long version = reader.GetInt64(1);
        if (version is < 1 or > int.MaxValue)
        {
            throw new AltimSchemaException(
                "The usage database records a schema version outside the range Altim uses. "
                + "Altim will not write to a database whose shape it cannot confirm.");
        }

        return (int)version;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void RecordVersion(SqliteConnection connection, int version)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM schema_version;
            INSERT INTO schema_version (version) VALUES ($version);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }
}
