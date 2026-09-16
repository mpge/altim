using Microsoft.Data.Sqlite;

namespace Altim.Storage;

/// <summary>
/// The one SQLite file Altim owns, and the only thing in the process that may write to
/// it. Writers are serialised behind a single connection and a gate; readers get their
/// own short-lived connections, which is what WAL is for.
/// </summary>
/// <remarks>
/// <para>
/// The file lives in the platform configuration directory: <c>%APPDATA%/Altim</c> on
/// Windows, <c>~/.config/altim</c> on Linux and
/// <c>~/Library/Application Support/Altim</c> on macOS. Tests pass an explicit path so
/// they never touch the real one.
/// </para>
/// <para>
/// Nothing here is a general SQLite host: <see cref="OpenForeignReadOnly"/> exists so a
/// provider can read a database another application owns, and it opens with
/// <see cref="SqliteOpenMode.ReadOnly"/> and <c>query_only</c>, so that reading someone
/// else's file cannot turn into writing to it.
/// </para>
/// <para>
/// Microsoft.Data.Sqlite executes every statement inline, so the methods here that
/// touch the file do their work on the thread that calls them. Each one says so.
/// </para>
/// </remarks>
public sealed class AltimDatabase : IDisposable
{
    /// <summary>
    /// The database file name, inside the platform configuration directory.
    /// </summary>
    public const string FileName = "altim.db";

    private const int CommandTimeoutSeconds = 30;
    private const int BusyTimeoutMilliseconds = 5_000;

    // Someone else's database is not worth waiting on. A provider read happens on a
    // scheduler tick, and a tick that stalls for half a minute because another
    // application is mid-write is worse than a tick that reports nothing this time.
    private const int ForeignCommandTimeoutSeconds = 2;
    private const int ForeignBusyTimeoutMilliseconds = 1_000;

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    /// <summary>
    /// How long <see cref="Dispose"/> waits for a write already in flight. Long enough
    /// for a transaction to finish, short enough that shutdown never appears to hang.
    /// </summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SqliteConnection _writer;
    private readonly string _readConnectionString;
    private int _disposed;

    private AltimDatabase(string databasePath, SqliteConnection writer, string readConnectionString,
                          int schemaVersion)
    {
        DatabasePath = databasePath;
        SchemaVersion = schemaVersion;
        _writer = writer;
        _readConnectionString = readConnectionString;
    }

    /// <summary>
    /// The absolute path of the open file.
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// The schema version the file was brought to when it was opened.
    /// </summary>
    public int SchemaVersion { get; }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// The directory Altim keeps its database and settings in on this platform.
    /// </summary>
    /// <returns>An absolute path. The directory is not created by this call.</returns>
    public static string GetDefaultDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(GetFolder(Environment.SpecialFolder.ApplicationData), "Altim");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(GetFolder(Environment.SpecialFolder.UserProfile), "Library",
                                "Application Support", "Altim");
        }

        string configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? string.Empty;
        string configRoot = configured.Length > 0
            ? configured
            : Path.Combine(GetFolder(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(configRoot, "altim");
    }

    /// <summary>
    /// The default location of the database file on this platform.
    /// </summary>
    /// <returns>An absolute path. Nothing is created by this call.</returns>
    public static string GetDefaultDatabasePath() => Path.Combine(GetDefaultDirectory(), FileName);

    /// <summary>
    /// Opens, creating and migrating as needed, the database at the default location.
    /// </summary>
    /// <returns>The open database.</returns>
    /// <remarks>
    /// Does real work on the calling thread: file creation, WAL setup and every
    /// migration the file still needs. Use <see cref="OpenAsync(CancellationToken)"/>
    /// from anything that must not block, which on this application means the UI thread.
    /// </remarks>
    public static AltimDatabase Open() => Open(GetDefaultDatabasePath());

    /// <summary>
    /// Opens, creating and migrating as needed, the database at an explicit location.
    /// </summary>
    /// <param name="databasePath">Where the file lives. Its directory is created if missing.</param>
    /// <returns>The open database, at <see cref="Migrations.LatestVersion"/>.</returns>
    /// <exception cref="AltimSchemaException">
    /// The file is at a schema version this build does not understand. Nothing is written
    /// in that case: an unreadable database is left exactly as it was found.
    /// </exception>
    /// <remarks>
    /// Does real work on the calling thread. See
    /// <see cref="OpenAsync(string, CancellationToken)"/>.
    /// </remarks>
    public static AltimDatabase Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string fullPath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writer = new SqliteConnection(ConnectionString(fullPath, SqliteOpenMode.ReadWriteCreate,
                                                           CommandTimeoutSeconds));
        try
        {
            writer.Open();
            ConfigureWriter(writer);
            int version = Migrations.Apply(writer);

            return new AltimDatabase(fullPath, writer,
                                     ConnectionString(fullPath, SqliteOpenMode.ReadWrite,
                                                      CommandTimeoutSeconds),
                                     version);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the database at the default location without occupying the calling thread.
    /// </summary>
    /// <param name="ct">Cancels the wait for a worker, not an open already running.</param>
    /// <returns>The open database.</returns>
    public static ValueTask<AltimDatabase> OpenAsync(CancellationToken ct)
        => OpenAsync(GetDefaultDatabasePath(), ct);

    /// <summary>
    /// Opens the database at an explicit location without occupying the calling thread.
    /// </summary>
    /// <param name="databasePath">Where the file lives. Its directory is created if missing.</param>
    /// <param name="ct">Cancels the wait for a worker, not an open already running.</param>
    /// <returns>The open database, at <see cref="Migrations.LatestVersion"/>.</returns>
    /// <remarks>
    /// Opening is the heaviest thing this type does — it creates the file, converts it
    /// to WAL and runs every outstanding migration — so it runs on a worker. Every
    /// exception <see cref="Open(string)"/> raises arrives here unchanged.
    /// </remarks>
    public static async ValueTask<AltimDatabase> OpenAsync(string databasePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        return await Task.Run(() => Open(databasePath), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a database belonging to another application for reading.
    /// </summary>
    /// <param name="databasePath">The other application's database file.</param>
    /// <returns>
    /// An open connection that cannot write: the file is opened
    /// <see cref="SqliteOpenMode.ReadOnly"/> and the connection additionally runs with
    /// <c>query_only</c>, so neither a mistake nor a migration can modify it. The caller
    /// disposes it.
    /// </returns>
    /// <exception cref="FileNotFoundException">
    /// There is no file there. The message deliberately omits the path, because where a
    /// provider keeps its data is part of the user's filesystem layout.
    /// </exception>
    /// <remarks>
    /// <para>
    /// No migration, no pragma that writes, and no schema check runs here. Altim is a
    /// guest in someone else's file and behaves like one.
    /// </para>
    /// <para>
    /// Commands on this connection give up after <see cref="ForeignCommandTimeoutSeconds"/>
    /// seconds rather than the 30 Altim allows itself, so a foreign database locked by
    /// its owner fails the tick instead of stalling it. Use
    /// <see cref="TryOpenForeignReadOnly"/> where "not available right now" is an
    /// ordinary answer.
    /// </para>
    /// </remarks>
    public static SqliteConnection OpenForeignReadOnly(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The database to read does not exist.");
        }

        var connection = new SqliteConnection(ConnectionString(fullPath, SqliteOpenMode.ReadOnly,
                                                                ForeignCommandTimeoutSeconds));
        try
        {
            connection.Open();
            ConfigureReader(connection, ForeignBusyTimeoutMilliseconds);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens another application's database for reading, treating "someone else has it"
    /// as an ordinary answer rather than a failure.
    /// </summary>
    /// <param name="databasePath">The other application's database file.</param>
    /// <returns>
    /// A read-only connection, or <see langword="null"/> when there is no file there or
    /// its owner is holding it. The caller disposes a non-null result.
    /// </returns>
    /// <remarks>
    /// This is the shape a scheduler tick wants: the connection is proved readable
    /// before it is handed back, with a bounded wait, so a provider either gets a
    /// usable connection promptly or reports the metric unavailable for this tick and
    /// tries again on the next one. A database in WAL mode — the common case — is
    /// readable even while its owner writes, so this normally succeeds.
    /// </remarks>
    public static SqliteConnection? TryOpenForeignReadOnly(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        SqliteConnection? connection = null;
        try
        {
            connection = OpenForeignReadOnly(databasePath);

            // Reading the schema takes the same shared lock the provider's own queries
            // will, so a file whose owner holds it exclusively is refused here rather
            // than three statements later.
            using SqliteCommand probe = connection.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_schema";
            _ = probe.ExecuteScalar();

            return connection;
        }
        catch (FileNotFoundException)
        {
            connection?.Dispose();
            return null;
        }
        catch (SqliteException error) when (error.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            connection?.Dispose();
            return null;
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a short-lived connection for reading Altim's own database. WAL lets these
    /// run while a write is in flight.
    /// </summary>
    /// <returns>
    /// An open connection with <c>query_only</c> set, so a read path cannot write by
    /// accident. The caller disposes it.
    /// </returns>
    /// <remarks>
    /// Opens the file on the calling thread. Callers that are already off the UI thread
    /// — which is every read path in this assembly — use it directly.
    /// </remarks>
    public SqliteConnection OpenRead()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var connection = new SqliteConnection(_readConnectionString);
        try
        {
            connection.Open();
            ConfigureReader(connection, BusyTimeoutMilliseconds);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes the single writer. Every write in the process goes through here, so two
    /// callers never contend for the file and SQLITE_BUSY is not a normal outcome.
    /// </summary>
    /// <param name="ct">Cancels the wait for the writer, not a write already running.</param>
    /// <returns>
    /// A lease holding the write connection. Disposing it hands the writer to the next
    /// caller, so a lease is held for as short a time as the work allows.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// The database was disposed, either before the wait started or while it was
    /// waiting. A caller parked here when the application shuts down is woken and told
    /// so; it is never handed a closed connection.
    /// </exception>
    public async ValueTask<WriteLease> LeaseWriterAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);

        if (IsDisposed)
        {
            // Hand the gate straight on so the next waiter learns the same thing.
            ReleaseWriter();
            throw new ObjectDisposedException(nameof(AltimDatabase));
        }

        return new WriteLease(this, _writer);
    }

    /// <summary>
    /// Closes the writer and refuses every later caller. Read connections already handed
    /// out are owned by their callers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate is taken first, with a bounded wait, so a write in flight finishes its
    /// transaction rather than having its connection closed underneath it. If the wait
    /// runs out the writer is closed anyway: shutdown is not allowed to hang on a stuck
    /// operation.
    /// </para>
    /// <para>
    /// The gate itself is deliberately not disposed. A <see cref="SemaphoreSlim"/> that
    /// has never handed out its wait handle holds no unmanaged resource, while disposing
    /// one out from under a parked waiter is precisely how a shutdown hangs or how a
    /// lease's release throws and hides the error that caused it.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        bool held = _writeGate.Wait(DisposeWait);

        _writer.Dispose();

        if (held)
        {
            // Wake whoever is parked in LeaseWriterAsync. They re-check, pass the gate
            // along and throw, instead of waiting for a writer that will never come.
            _writeGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    internal void ReleaseWriter()
    {
        try
        {
            _writeGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // The database went away underneath the lease. Releasing a gate that no
            // longer exists is not an error worth throwing over the caller's real one.
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static string GetFolder(Environment.SpecialFolder folder)
        => Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    private static string ConnectionString(string path, SqliteOpenMode mode, int timeoutSeconds)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = timeoutSeconds,

            // Pooling off: a pooled connection keeps the file handle open after Dispose,
            // which makes the file impossible to delete deterministically. Altim opens a
            // handful of connections a minute, so the pool buys nothing and costs tidiness.
            Pooling = false,
        };

        return builder.ToString();
    }

    private static void ConfigureWriter(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();

        // busy_timeout first: converting a fresh file to WAL needs the file to itself for
        // a moment, and two instances starting together would otherwise have one of them
        // fail on that very first statement.
        command.CommandText = $"""
            PRAGMA busy_timeout = {BusyTimeoutMilliseconds};
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            """;
        command.ExecuteNonQuery();
    }

    private static void ConfigureReader(SqliteConnection connection, int busyTimeoutMilliseconds)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA busy_timeout = {busyTimeoutMilliseconds};
            PRAGMA foreign_keys = ON;
            PRAGMA query_only = ON;
            """;
        command.ExecuteNonQuery();
    }
}
