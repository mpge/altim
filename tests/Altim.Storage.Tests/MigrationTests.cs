using Microsoft.Data.Sqlite;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// Opening a database is the one operation that can destroy a user's history, so it is
/// checked against a real file: created from nothing, opened again, and refused when it
/// says something this build does not understand.
/// </summary>
public sealed class MigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The version table a test ladder's first rung has to create, exactly as the real
    /// first rung does: the ladder records where it has got to inside the database it is
    /// building, so rung 1 owns that table.
    /// </summary>
    private const string VersionTable = "CREATE TABLE schema_version (version INTEGER NOT NULL);";

    [Fact]
    public void ACreatedDatabaseIsAtTheCurrentSchemaVersion()
    {
        using var temp = new TempDatabase();

        AltimDatabase database = temp.Open();

        Assert.Equal(SqliteSchema.CurrentVersion, database.SchemaVersion);
        Assert.Equal((long)SqliteSchema.CurrentVersion,
                     temp.ScalarInt64("SELECT version FROM schema_version"));
        Assert.Equal(1L, temp.CountRows("schema_version"));
    }

    [Fact]
    public void ACreatedDatabaseHasEveryTableAndIndex()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();

        Assert.Equal(1L, temp.ScalarInt64(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'usage_sample'"));
        Assert.Equal(1L, temp.ScalarInt64(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'setting'"));
        Assert.Equal(1L, temp.ScalarInt64(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'notification_state'"));
        Assert.Equal(1L, temp.ScalarInt64(
            "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_usage_sample_lookup'"));
    }

    [Fact]
    public void TheLatestMigrationMatchesTheDeclaredSchemaVersion()
        => Assert.Equal(SqliteSchema.CurrentVersion, Migrations.LatestVersion);

    [Fact]
    public void ReopeningAnExistingDatabaseChangesNothing()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();

        temp.Execute("""
            INSERT INTO usage_sample (provider_id, metric_key, captured_at, used_percent)
            VALUES ('claude', 'five_hour', 1763000000, 42.5);
            """);

        AltimDatabase reopened = temp.Open();

        Assert.Equal(SqliteSchema.CurrentVersion, reopened.SchemaVersion);
        Assert.Equal(1L, temp.CountRows("schema_version"));
        Assert.Equal(1L, temp.CountRows("usage_sample"));
        Assert.Equal(42.5, temp.ScalarDouble("SELECT used_percent FROM usage_sample"));
    }

    [Fact]
    public void ADatabaseFromANewerBuildIsRefused()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();
        temp.Close();

        temp.Execute("UPDATE schema_version SET version = 99");

        AltimSchemaException error = Assert.Throws<AltimSchemaException>(() => temp.Open());

        Assert.Equal(99, error.FoundVersion);
        Assert.Equal(Migrations.LatestVersion, error.SupportedVersion);

        // Refused means untouched: the version it was written with is still there.
        Assert.Equal(99L, temp.ScalarInt64("SELECT version FROM schema_version"));
    }

    [Fact]
    public void ADatabaseWithNoRecordedVersionIsRefused()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();
        temp.Close();

        temp.Execute("DELETE FROM schema_version");

        _ = Assert.Throws<AltimSchemaException>(() => temp.Open());
    }

    [Fact]
    public void ASchemaVersionThatIsNotAWholeNumberIsRefused()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();
        temp.Close();

        // SQLite stores what it is given: an INTEGER column holds "one" happily, and a
        // typed read of it comes back as 0, which is what an empty file looks like.
        temp.Execute("UPDATE schema_version SET version = 'one'");

        AltimSchemaException error = Assert.Throws<AltimSchemaException>(() => temp.Open());

        // Refused means untouched: the nonsense is still there and so are the tables.
        Assert.Null(error.FoundVersion);
        Assert.Equal("one", Assert.IsType<string>(temp.Scalar("SELECT version FROM schema_version")));
        Assert.Equal(1L, temp.ScalarInt64(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'usage_sample'"));
    }

    [Fact]
    public void ASchemaVersionOfZeroIsRefusedRatherThanReAppliedOverTheTables()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();
        temp.Close();

        temp.Execute("UPDATE schema_version SET version = 0");

        _ = Assert.Throws<AltimSchemaException>(() => temp.Open());
    }

    [Fact]
    public void ARungAppliedByAnotherInstanceMidOpenIsSkippedRatherThanRepeated()
    {
        using var temp = new TempDatabase();

        Migrations.Migration[] ladder =
        [
            new Migrations.Migration(1, VersionTable + "CREATE TABLE first (x INTEGER);"),
            new Migrations.Migration(2, "CREATE TABLE second (x INTEGER);"),
        ];

        using SqliteConnection slow = OpenRaw(temp.FilePath);

        // The other instance: it climbs the whole ladder in the gap between this one
        // reading the version and opening its first rung's transaction.
        bool raced = false;
        void Race()
        {
            if (raced)
            {
                return;
            }

            raced = true;

            using SqliteConnection fast = OpenRaw(temp.FilePath);
            Assert.Equal(2, Migrations.Apply(fast, ladder));
        }

        Assert.Equal(2, Migrations.Apply(slow, ladder, Race));

        Assert.True(raced);
        Assert.Equal(2L, ScalarInt64(slow, "SELECT version FROM schema_version"));
        Assert.Equal(1L, ScalarInt64(slow, "SELECT count(*) FROM schema_version"));
    }

    [Fact]
    public void SeveralInstancesStartingAtOnceAllOpenTheSameDatabase()
    {
        using var temp = new TempDatabase();

        const int Instances = 4;

        // Real threads and a barrier: the point is that they are inside Open at the same
        // moment, which a thread pool is free not to arrange.
        using var start = new Barrier(Instances);
        var opened = new AltimDatabase?[Instances];
        var failed = new Exception?[Instances];

        var instances = new Thread[Instances];
        for (int index = 0; index < Instances; index++)
        {
            int slot = index;
            instances[slot] = new Thread(() =>
            {
                start.SignalAndWait();

                try
                {
                    opened[slot] = AltimDatabase.Open(temp.FilePath);
                }
                catch (Exception error)
                {
                    failed[slot] = error;
                }
            })
            {
                IsBackground = true,
                Name = $"altim-instance-{slot}",
            };

            instances[slot].Start();
        }

        try
        {
            foreach (Thread instance in instances)
            {
                Assert.True(instance.Join(TimeSpan.FromSeconds(60)), "an instance never finished");
            }

            Assert.All(failed, Assert.Null);
            Assert.All(opened, instance => Assert.Equal(SqliteSchema.CurrentVersion,
                                                        Assert.IsType<AltimDatabase>(instance)
                                                              .SchemaVersion));

            Assert.Equal(1L, temp.CountRows("schema_version"));
            Assert.Equal((long)SqliteSchema.CurrentVersion,
                         temp.ScalarInt64("SELECT version FROM schema_version"));
        }
        finally
        {
            foreach (AltimDatabase? instance in opened)
            {
                instance?.Dispose();
            }
        }
    }

    [Fact]
    public void RungsAtOrBelowTheCurrentVersionAreSkipped()
    {
        using var temp = new TempDatabase();
        using SqliteConnection connection = OpenRaw(temp.FilePath);

        Assert.Equal(1, Migrations.Apply(connection,
                                         [new Migrations.Migration(1, VersionTable + "CREATE TABLE first (x);")]));

        // Rung 1 would fail outright if it ran again, which is the point: it must not.
        Migrations.Migration[] longer =
        [
            new Migrations.Migration(1, VersionTable + "CREATE TABLE first (x);"),
            new Migrations.Migration(2, "CREATE TABLE second (x);"),
        ];

        Assert.Equal(2, Migrations.Apply(connection, longer));
        Assert.Equal(2, Migrations.ReadVersion(connection));
    }

    [Fact]
    public void HigherRungsApplyInOrder()
    {
        using var temp = new TempDatabase();
        using SqliteConnection connection = OpenRaw(temp.FilePath);

        // Rung 2 and rung 3 only work if rung 1, then rung 2, have already run.
        Migrations.Migration[] ladder =
        [
            new Migrations.Migration(1, VersionTable + "CREATE TABLE climbed (step INTEGER);"),
            new Migrations.Migration(2, "ALTER TABLE climbed ADD COLUMN second TEXT;"),
            new Migrations.Migration(3, "INSERT INTO climbed (step, second) VALUES (3, 'done');"),
        ];

        Assert.Equal(3, Migrations.Apply(connection, ladder));

        Assert.Equal(3, Migrations.ReadVersion(connection));
        Assert.Equal("done", Assert.IsType<string>(
            Scalar(connection, "SELECT second FROM climbed WHERE step = 3")));
    }

    [Fact]
    public void ARungThatFailsAfterItsDdlLeavesTheDatabaseAtThePreviousRung()
    {
        using var temp = new TempDatabase();
        using SqliteConnection connection = OpenRaw(temp.FilePath);

        Migrations.Migration[] ladder =
        [
            new Migrations.Migration(1, VersionTable + "CREATE TABLE settled (x INTEGER);"),
            new Migrations.Migration(2, """
                CREATE TABLE halfway (x INTEGER NOT NULL);
                INSERT INTO halfway (x) VALUES (NULL);
                """),
        ];

        _ = Assert.Throws<SqliteException>(() => Migrations.Apply(connection, ladder));

        Assert.Equal(1, Migrations.ReadVersion(connection));
        Assert.Equal(1L, ScalarInt64(connection,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'settled'"));
        Assert.Equal(0L, ScalarInt64(connection,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'halfway'"));
    }

    [Fact]
    public void AnEmptyFileIsReportedAsVersionZero()
    {
        using var temp = new TempDatabase();
        temp.Execute("SELECT 1");

        using SqliteConnection connection = OpenRaw(temp.FilePath);

        Assert.Equal(0, Migrations.ReadVersion(connection));
    }

    [Fact]
    public async Task TheWriterRunsInWalWithForeignKeysOn()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        using WriteLease lease = await database.LeaseWriterAsync(Ct);

        using SqliteCommand journal = lease.Connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", Assert.IsType<string>(journal.ExecuteScalar()));

        using SqliteCommand keys = lease.Connection.CreateCommand();
        keys.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, Assert.IsType<long>(keys.ExecuteScalar()));
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static long ScalarInt64(SqliteConnection connection, string sql)
        => Assert.IsType<long>(Scalar(connection, sql));

    private static SqliteConnection OpenRaw(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }
}
