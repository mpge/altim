using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// Connection management: where the file goes, that only one writer exists, and that
/// every read path is incapable of writing.
/// </summary>
public sealed class AltimDatabaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheDefaultPathIsTheDatabaseInsideThePlatformConfigDirectory()
    {
        string directory = AltimDatabase.GetDefaultDirectory();
        string path = AltimDatabase.GetDefaultDatabasePath();

        Assert.True(Path.IsPathRooted(directory));
        Assert.Equal(Path.Combine(directory, AltimDatabase.FileName), path);
        Assert.Equal(AltimDatabase.FileName, Path.GetFileName(path));
        Assert.Equal(OperatingSystem.IsLinux() ? "altim" : "Altim",
                     Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void OpeningCreatesTheDirectoryAndTheFile()
    {
        using var temp = new TempDatabase();

        AltimDatabase database = temp.Open();

        Assert.True(File.Exists(temp.FilePath));
        Assert.Equal(Path.GetFullPath(temp.FilePath), database.DatabasePath);
    }

    [Fact]
    public async Task OnlyOneWriterIsHandedOutAtATime()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        using WriteLease first = await database.LeaseWriterAsync(Ct);

        ValueTask<WriteLease> queued = database.LeaseWriterAsync(Ct);
        Assert.False(queued.IsCompleted);

        first.Dispose();

        using WriteLease second = await queued;
        Assert.Same(first.Connection, second.Connection);
    }

    [Fact]
    public void ReadConnectionsRefuseToWrite()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        using SqliteConnection connection = database.OpenRead();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM usage_sample";

        SqliteException error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("readonly", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AForeignDatabaseCanBeReadWithoutBeingWrittenTo()
    {
        using var temp = new TempDatabase();

        // Stand in for another application's database: a shape Altim does not own.
        temp.Execute("""
            CREATE TABLE quota (id INTEGER PRIMARY KEY, used_percent REAL NOT NULL);
            INSERT INTO quota (used_percent) VALUES (63.5);
            """);

        using SqliteConnection connection = AltimDatabase.OpenForeignReadOnly(temp.FilePath);

        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT used_percent FROM quota";
        Assert.Equal(63.5, Assert.IsType<double>(read.ExecuteScalar()));

        using SqliteCommand write = connection.CreateCommand();
        write.CommandText = "UPDATE quota SET used_percent = 0";
        _ = Assert.Throws<SqliteException>(() => write.ExecuteNonQuery());

        Assert.Equal(63.5, temp.ScalarDouble("SELECT used_percent FROM quota"));
    }

    [Fact]
    public void ReadingAForeignDatabaseThatIsNotThereFailsWithoutNamingIt()
    {
        using var temp = new TempDatabase();

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => AltimDatabase.OpenForeignReadOnly(temp.FilePath));

        Assert.DoesNotContain(temp.FilePath, error.Message, StringComparison.Ordinal);
        Assert.Null(error.FileName);
    }

    [Fact]
    public async Task UsingADisposedDatabaseIsRejected()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        temp.Close();

        _ = Assert.Throws<ObjectDisposedException>(() => database.OpenRead().Dispose());
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await database.LeaseWriterAsync(Ct));
    }

    [Fact]
    public async Task DisposingWaitsForAWriteThatIsAlreadyInFlight()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        WriteLease lease = await database.LeaseWriterAsync(Ct);

        Task shutdown = Task.Run(database.Dispose, Ct);

        // The gate is held, so shutdown waits instead of closing the connection out from
        // under the write.
        await AssertStillWaiting(shutdown);

        using (SqliteCommand command = lease.Connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO usage_sample (provider_id, metric_key, captured_at, used_percent)
                VALUES ('claude', 'five_hour', 1763000000, 12.5)
                """;
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        // Handing the writer back is what lets shutdown finish, and it must not throw.
        lease.Dispose();

        await shutdown.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(1L, temp.CountRows("usage_sample"));
    }

    [Fact]
    public async Task AWaiterIsToldTheDatabaseWentAwayRatherThanBeingLeftParked()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        WriteLease held = await database.LeaseWriterAsync(Ct);

        ValueTask<WriteLease> queued = database.LeaseWriterAsync(Ct);
        Assert.False(queued.IsCompleted);

        Task shutdown = Task.Run(database.Dispose, Ct);
        await AssertStillWaiting(shutdown);

        held.Dispose();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => _ = await queued);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        using (WriteLease lease = await database.LeaseWriterAsync(Ct))
        {
            Assert.NotNull(lease.Connection);
        }

        database.Dispose();
        database.Dispose();
    }

    [Fact]
    public async Task OpeningDoesNotRunOnTheCallingThread()
    {
        using var temp = new TempDatabase();

        ValueTask<AltimDatabase> opening = AltimDatabase.OpenAsync(temp.FilePath, Ct);

        // Creating the file, converting it to WAL and running the ladder is the heaviest
        // thing here; the caller gets its thread back rather than wearing all of it.
        Assert.False(opening.IsCompleted);

        AltimDatabase database = await opening;
        try
        {
            Assert.Equal(SqliteSchema.CurrentVersion, database.SchemaVersion);
        }
        finally
        {
            database.Dispose();
        }
    }

    [Fact]
    public void AForeignDatabaseInWalModeIsReadableWhileItsOwnerIsWriting()
    {
        using var temp = new TempDatabase();

        // The realistic case: another application's database, in WAL, with a write in
        // flight. WAL is what makes reading it safe, and Altim only ever reads.
        using (SqliteConnection owner = OpenForeign(temp.FilePath))
        {
            Execute(owner, """
                PRAGMA journal_mode = WAL;
                CREATE TABLE quota (id INTEGER PRIMARY KEY, used_percent REAL NOT NULL);
                INSERT INTO quota (used_percent) VALUES (63.5);
                """);

            using SqliteTransaction inFlight = owner.BeginTransaction();
            Execute(owner, "UPDATE quota SET used_percent = 99");

            using SqliteConnection reader = Assert.IsType<SqliteConnection>(
                AltimDatabase.TryOpenForeignReadOnly(temp.FilePath));

            // The committed value, promptly, with the owner's uncommitted one invisible.
            Assert.Equal(63.5, Assert.IsType<double>(
                Scalar(reader, "SELECT used_percent FROM quota")));

            inFlight.Rollback();
        }

        Assert.Equal(63.5, temp.ScalarDouble("SELECT used_percent FROM quota"));
    }

    [Fact]
    public void AForeignDatabaseAnotherApplicationIsHoldingIsUnavailableNotASourceOfDelay()
    {
        using var temp = new TempDatabase();

        using SqliteConnection owner = OpenForeign(temp.FilePath);
        Execute(owner, """
            CREATE TABLE quota (id INTEGER PRIMARY KEY, used_percent REAL NOT NULL);
            INSERT INTO quota (used_percent) VALUES (63.5);
            """);

        // No WAL here, and an exclusive lock: the file is genuinely unreadable until its
        // owner is done with it.
        Execute(owner, "BEGIN EXCLUSIVE; UPDATE quota SET used_percent = 1;");

        long started = Stopwatch.GetTimestamp();
        SqliteConnection? reader = AltimDatabase.TryOpenForeignReadOnly(temp.FilePath);
        TimeSpan waited = Stopwatch.GetElapsedTime(started);

        reader?.Dispose();
        Execute(owner, "ROLLBACK");

        Assert.Null(reader);

        // A scheduler tick is a minute apart. Anything near the 30 seconds Altim allows
        // its own commands would stall the tick and the next one after it.
        Assert.True(waited < TimeSpan.FromSeconds(10), $"waited {waited.TotalSeconds:0.0}s");
    }

    [Fact]
    public void AForeignDatabaseThatIsNotThereIsUnavailableRatherThanAnError()
    {
        using var temp = new TempDatabase();

        Assert.Null(AltimDatabase.TryOpenForeignReadOnly(temp.FilePath));
    }

    private static async Task AssertStillWaiting(Task shutdown)
    {
        Task first = await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromMilliseconds(250), Ct));

        Assert.NotSame(shutdown, first);
    }

    private static SqliteConnection OpenForeign(string path)
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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
