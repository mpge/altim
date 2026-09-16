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
/// </remarks>
public sealed class AltimDatabase : IDisposable
{
    /// <summary>
    /// The database file name, inside the platform configuration directory.
    /// </summary>
    public const string FileName = "altim.db";

    private const int CommandTimeoutSeconds = 30;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SqliteConnection _writer;
    private readonly string _readConnectionString;
    private bool _disposed;

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
    public static AltimDatabase Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string fullPath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writer = new SqliteConnection(ConnectionString(fullPath, SqliteOpenMode.ReadWriteCreate));
        try
        {
            writer.Open();
            ConfigureWriter(writer);
            int version = Migrations.Apply(writer);

            return new AltimDatabase(fullPath, writer,
                                     ConnectionString(fullPath, SqliteOpenMode.ReadWrite), version);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
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
    /// No migration, no pragma that writes, and no schema check runs here. Altim is a
    /// guest in someone else's file and behaves like one.
    /// </remarks>
    public static SqliteConnection OpenForeignReadOnly(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The database to read does not exist.");
        }

        var connection = new SqliteConnection(ConnectionString(fullPath, SqliteOpenMode.ReadOnly));
        try
        {
            connection.Open();
            ConfigureReader(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
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
    public SqliteConnection OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = new SqliteConnection(_readConnectionString);
        try
        {
            connection.Open();
            ConfigureReader(connection);
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
    public async ValueTask<WriteLease> LeaseWriterAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        return new WriteLease(this, _writer);
    }

    /// <summary>
    /// Closes the writer and releases the gate. Read connections already handed out are
    /// owned by their callers.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();
        _writeGate.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void ReleaseWriter() => _writeGate.Release();

    private static string GetFolder(Environment.SpecialFolder folder)
        => Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    private static string ConnectionString(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = CommandTimeoutSeconds,

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
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        command.ExecuteNonQuery();
    }

    private static void ConfigureReader(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA query_only = ON;
            """;
        command.ExecuteNonQuery();
    }
}
