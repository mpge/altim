using Microsoft.Data.Sqlite;

namespace Altim.Storage;

/// <summary>
/// Exclusive hold on the process's single write connection, taken with
/// <see cref="AltimDatabase.LeaseWriterAsync"/> and handed back on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// The lease does not own the connection and never closes it. Disposing twice is
/// harmless; never disposing starves the next writer, which is why every use site is a
/// <c>using</c> declaration. Disposing after the database itself has been disposed is
/// harmless too: the release is swallowed rather than allowed to throw over whatever
/// the caller was already dealing with.
/// </remarks>
public sealed class WriteLease : IDisposable
{
    private AltimDatabase? _owner;

    internal WriteLease(AltimDatabase owner, SqliteConnection connection)
    {
        _owner = owner;
        Connection = connection;
    }

    /// <summary>
    /// The write connection. Commands are created from it with
    /// <see cref="SqliteConnection.CreateCommand"/>, which attaches whatever transaction
    /// the connection currently has open.
    /// </summary>
    public SqliteConnection Connection { get; }

    /// <summary>
    /// Releases the writer to the next caller.
    /// </summary>
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseWriter();
}
