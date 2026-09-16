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
/// Adding a migration is one line: append <c>new Migration(2, "...")</c> to
/// <see cref="Ordered"/> and raise <see cref="SqliteSchema.CurrentVersion"/> to match.
/// Nothing else changes, and <see cref="SqliteSchema.CreateScript"/> is never edited,
/// because an existing database only ever sees the new rung.
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
    private sealed record Migration(int Version, string Sql);

    /// <summary>
    /// Every migration, oldest first. This ordering is the schema's history and is
    /// append only.
    /// </summary>
    private static readonly Migration[] Ordered =
    [
        new Migration(1, SqliteSchema.CreateScript),
    ];

    /// <summary>
    /// The newest version this build can bring a database to.
    /// </summary>
    public static int LatestVersion => Ordered[^1].Version;

    /// <summary>
    /// Brings an open connection's database up to <see cref="LatestVersion"/>.
    /// </summary>
    /// <param name="connection">An open read-write connection.</param>
    /// <returns>The version the database is at afterwards.</returns>
    /// <exception cref="AltimSchemaException">
    /// The database is at a version this build does not know, or its
    /// <c>schema_version</c> table does not hold exactly one row.
    /// </exception>
    public static int Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        int current = ReadVersion(connection);
        if (current > LatestVersion)
        {
            throw AltimSchemaException.FromNewerVersion(current, LatestVersion);
        }

        foreach (Migration migration in Ordered)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();

            Execute(connection, migration.Sql);
            RecordVersion(connection, migration.Version);

            transaction.Commit();
            current = migration.Version;
        }

        return current;
    }

    /// <summary>
    /// Reads the version recorded in a database.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <returns>
    /// The recorded version, or 0 when the database has no <c>schema_version</c> table
    /// at all, which is what an empty file looks like.
    /// </returns>
    /// <exception cref="AltimSchemaException">
    /// The table exists but does not hold exactly one row.
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
        read.CommandText = "SELECT version FROM schema_version";

        using SqliteDataReader reader = read.ExecuteReader();
        if (!reader.Read())
        {
            throw new AltimSchemaException(
                "The usage database records no schema version. Altim will not write to a "
                + "database whose shape it cannot confirm.");
        }

        int version = reader.GetInt32(0);
        if (reader.Read())
        {
            throw new AltimSchemaException(
                "The usage database records more than one schema version. Altim will not "
                + "write to a database whose shape it cannot confirm.");
        }

        return version;
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
