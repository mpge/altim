using System.Diagnostics;
using System.Globalization;
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

    /// <summary>
    /// The log is bounded while Altim is working, which is the half a caller cannot ask for.
    /// </summary>
    /// <remarks>
    /// The defect this covers: SQLite checkpoints the write-ahead log automatically but does
    /// not shrink it — it rewinds and writes over the same bytes — so the file settles at
    /// whatever high-water mark it ever reached and stays there. Against a database of a few
    /// hundred kilobytes that produced a multi-megabyte log. <c>journal_size_limit</c> is
    /// what turns the reset into a truncation, and it is only in force if it was set after
    /// the connection entered WAL mode, which is the mistake this asserts against.
    /// </remarks>
    [Fact]
    public async Task TheWriteAheadLogStaysBoundedUnderSustainedWriting()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        Assert.Equal("wal", Convert.ToString(temp.Scalar("PRAGMA journal_mode"), CultureInfo.InvariantCulture));

        string walPath = temp.FilePath + "-wal";

        // Many small transactions, which is the shape Altim writes in: one row at a time as
        // a reading changes, never a bulk load.
        for (int i = 0; i < 2000; i++)
        {
            using WriteLease lease = await database.LeaseWriterAsync(Ct);
            using SqliteCommand command = lease.Connection.CreateCommand();
            command.CommandText =
                "INSERT INTO usage_sample (provider_id, metric_key, captured_at, used_percent) " +
                "VALUES ('claude', 'five_hour', $at, $percent)";
            _ = command.Parameters.AddWithValue("$at", i);
            _ = command.Parameters.AddWithValue("$percent", i % 100);
            _ = command.ExecuteNonQuery();
        }

        long walBytes = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;

        // SQLite's own default would allow four megabytes here. The assertion is deliberately
        // loose — the log is allowed to be anywhere under the checkpoint threshold plus a
        // little slack — because the point is the order of magnitude, not an exact size.
        Assert.True(walBytes < 2 * 1024 * 1024, $"The write-ahead log reached {walBytes} bytes.");
    }

    /// <summary>
    /// And it goes to nothing once nothing is writing, which is the half a caller does ask
    /// for.
    /// </summary>
    [Fact]
    public async Task AnIdleDatabaseHasItsWriteAheadLogEmptied()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        using (WriteLease lease = await database.LeaseWriterAsync(Ct))
        {
            using SqliteCommand command = lease.Connection.CreateCommand();
            command.CommandText =
                "INSERT INTO usage_sample (provider_id, metric_key, captured_at, used_percent) " +
                "VALUES ('claude', 'five_hour', 1, 10)";
            _ = command.ExecuteNonQuery();
        }

        string walPath = temp.FilePath + "-wal";
        Assert.True(new FileInfo(walPath).Length > 0);

        // A window of zero: the write has just happened, so anything longer would make this
        // a test of the clock.
        Assert.Equal(WalCheckpoint.Truncated, await database.CheckpointIfIdleAsync(TimeSpan.Zero, Ct));
        Assert.Equal(0, new FileInfo(walPath).Length);

        // And the row is in the database rather than only in the log that was just emptied,
        // which is the thing a checkpoint must never get wrong.
        temp.Close();
        Assert.Equal(1, temp.CountRows("usage_sample"));
    }

    /// <summary>A database that has just been written to is left alone.</summary>
    [Fact]
    public async Task ADatabaseThatWasJustWrittenToIsNotCheckpointed()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();

        using (WriteLease lease = await database.LeaseWriterAsync(Ct))
        {
            using SqliteCommand command = lease.Connection.CreateCommand();
            command.CommandText =
                "INSERT INTO usage_sample (provider_id, metric_key, captured_at, used_percent) " +
                "VALUES ('claude', 'five_hour', 1, 10)";
            _ = command.ExecuteNonQuery();
        }

        Assert.Equal(WalCheckpoint.Skipped, await database.CheckpointIfIdleAsync(TimeSpan.FromMinutes(5), Ct));
        Assert.True(new FileInfo(temp.FilePath + "-wal").Length > 0);
    }

    /// <summary>
    /// Holding the writer is not writing, and the difference is what makes the checkpoint
    /// reachable at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The idle clock is read by <see cref="AltimDatabase.CheckpointIfIdleAsync"/>, and it
    /// used to be stamped when the writer was <em>taken</em>. Altim takes the writer
    /// constantly to find out there is nothing to do — the history service, the notification
    /// state, the down-sampler and the vacuum check — and the maintenance pass runs two of
    /// those in the same method that then asks whether two minutes have passed without a
    /// write. The answer was always no, so the checkpoint that empties the write-ahead log
    /// was unreachable: measured over nine minutes with both provider stores empty and
    /// nothing whatever to report, it never ran once.
    /// </para>
    /// <para>
    /// This is the maintenance pass, in its own order, against a database that has just been
    /// written to and then left alone.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task HoldingTheWriterWithNothingToWriteLeavesTheDatabaseIdle()
    {
        using var temp = new TempDatabase();
        AltimDatabase database = temp.Open();
        var retention = new UsageRetention(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan idleWindow = TimeSpan.FromMilliseconds(250);

        // The first vacuum check writes: it starts the clock rather than compacting.
        _ = await retention.VacuumIfDueAsync(now, TimeSpan.FromDays(30), Ct);

        // Inside the retention window, so the down-sampler below has nothing to collapse —
        // which is the ordinary case it runs in, since it only touches rows a month old.
        using (WriteLease lease = await database.LeaseWriterAsync(Ct))
        {
            Execute(lease.Connection,
                    "INSERT INTO usage_sample (provider_id, metric_key, captured_at, used_percent) " +
                    $"VALUES ('claude', 'five_hour', {now.ToUnixTimeSeconds()}, 10)");
        }

        // The control: something really was written, so the database is not idle.
        Assert.Equal(WalCheckpoint.Skipped, await database.CheckpointIfIdleAsync(idleWindow, Ct));

        await Task.Delay(TimeSpan.FromMilliseconds(600), Ct);

        // The maintenance pass, in order. Both take the writer; neither has anything to do.
        _ = await retention.DownsampleAsync(now, Ct);
        _ = await retention.VacuumIfDueAsync(now, TimeSpan.FromDays(30), Ct);

        Assert.NotEqual(WalCheckpoint.Skipped, await database.CheckpointIfIdleAsync(idleWindow, Ct));
    }

    /// <summary>
    /// The log line that says the database opened names the kind of directory, never the
    /// directory. On every platform Altim supports the configuration directory is inside the
    /// user's profile, so the path contains the account name — and <c>altim.log</c> is a file
    /// people attach to bug reports.
    /// </summary>
    [Fact]
    public void TheDirectoryIsDescribedByKindRatherThanByPath()
    {
        using var temp = new TempDatabase();

        string standard = AltimDatabase.DescribeDirectory(AltimDatabase.GetDefaultDatabasePath());
        string elsewhere = AltimDatabase.DescribeDirectory(temp.FilePath);

        Assert.Equal("the default configuration directory", standard);
        Assert.NotEqual(standard, elsewhere);

        foreach (string described in new[] { standard, elsewhere })
        {
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), described, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.UserName, described, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Altim", described, StringComparison.Ordinal);
        }

        // Not a whole-path comparison: a file beside the real one is still the standard
        // directory, and a file of the standard name somewhere else is not.
        Assert.Equal(
            standard,
            AltimDatabase.DescribeDirectory(
                Path.Combine(AltimDatabase.GetDefaultDirectory(), "altim.backup.db")));
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
