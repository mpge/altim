using Altim.App.Diagnostics;
using Altim.App.Services;
using Altim.Core.Abstractions;
using Altim.Core.Settings;
using Altim.Storage;

namespace Altim.App.Composition;

/// <summary>
/// The SQLite database and everything built on it, or the in-memory stand-ins for a machine
/// where it could not be opened.
/// </summary>
/// <remarks>
/// An unreadable database is a degraded state, not a failure to start: history stops being
/// recorded, settings stop surviving a restart, and the reason appears in the tray menu. The
/// tray icon, the popup and both providers carry on exactly as before, because none of them
/// reads the database on the path that produces a number.
/// </remarks>
internal sealed class StorageStack : IDisposable
{
    private readonly AltimDatabase? _database;
    private bool _disposed;

    private StorageStack(AltimDatabase? database, IUsageHistoryService history, ISettingsBackend settings)
    {
        _database = database;
        History = history;
        Settings = settings;
    }

    /// <summary>Where readings are recorded. Never null; a no-op when storage is unavailable.</summary>
    public IUsageHistoryService History { get; }

    /// <summary>Where settings live. Never null; in memory when storage is unavailable.</summary>
    public ISettingsBackend Settings { get; }

    /// <summary>
    /// The persisted record of which notifications have fired, or null when storage is
    /// unavailable, in which case the state lives in memory and start-up silence is the
    /// only de-duplication that survives a restart.
    /// </summary>
    public NotificationStateStore? NotificationState { get; private init; }

    /// <summary>The down-sampler and compactor, or null when storage is unavailable.</summary>
    public UsageRetention? Retention { get; private init; }

    /// <summary>
    /// Raw key/value access to the <c>setting</c> table, or null when storage is
    /// unavailable.
    /// </summary>
    /// <remarks>
    /// <see cref="Settings"/> is the typed settings record and is all the rest of the
    /// application wants. The maintenance pass keeps a couple of scalars that are not
    /// settings at all — when each provider's backfill last ran — and they belong in the
    /// same table beside the compaction stamp rather than in a file of their own. Null
    /// here means those stamps do not survive a restart, which degrades to asking a
    /// provider for its history once per run instead of once per day.
    /// </remarks>
    public SqliteSettingsStore? Scalars { get; private init; }

    /// <summary>The open database, or null when it could not be opened.</summary>
    public AltimDatabase? Database => _database;

    /// <summary>The database file, or null when it could not be opened.</summary>
    public string? DatabasePath => _database?.DatabasePath;

    /// <summary>Opens the database, degrading to memory when it cannot be opened.</summary>
    /// <param name="report">Collects anything that had to be degraded.</param>
    /// <param name="ct">Cancels the open.</param>
    public static async ValueTask<StorageStack> OpenAsync(StartupReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            AltimDatabase database = await AltimDatabase.OpenAsync(ct).ConfigureAwait(false);

            // The kind of directory, never the directory. On every platform Altim supports
            // the configuration directory is inside the user's profile, so the path carries
            // the account name — and altim.log is a file people attach to bug reports.
            AltimLog.Write(
                "storage",
                "Opened the database in " + AltimDatabase.DescribeDirectory(database.DatabasePath) +
                " at schema version " +
                database.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var settings = new SqliteSettingsStore(database);

            return new StorageStack(
                database,
                new SqliteUsageHistoryService(database),
                new SqliteSettingsBackend(settings))
            {
                NotificationState = new NotificationStateStore(database),
                Retention = new UsageRetention(database),
                Scalars = settings,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            report.Add(
                "Usage history is unavailable, settings will not survive a restart, and "
                + "live quota checks and update checks are off until they can be read");
            AltimLog.Write("storage", "Opening the database failed", ex);

            return new StorageStack(null, new NullUsageHistoryService(), new MemorySettingsBackend());
        }
    }

    /// <summary>Closes the database.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _database?.Dispose();
        }
        catch (Exception ex)
        {
            AltimLog.Write("storage", "Closing the database failed", ex);
        }
    }

    /// <summary>Adapts the storage settings store onto the interface's seam shape.</summary>
    /// <param name="store">The store to forward to.</param>
    private sealed class SqliteSettingsBackend(SqliteSettingsStore store) : ISettingsBackend
    {
        /// <inheritdoc />
        public ValueTask<AltimSettings> GetAsync(CancellationToken ct) => store.GetAsync(ct);

        /// <inheritdoc />
        public ValueTask SaveAsync(AltimSettings settings, CancellationToken ct) => store.SaveAsync(settings, ct);
    }
}
