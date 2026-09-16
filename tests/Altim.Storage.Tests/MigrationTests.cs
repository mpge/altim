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

    private static SqliteConnection OpenRaw(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }
}
