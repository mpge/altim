using Microsoft.Data.Sqlite;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// A real database file in a directory of its own, deleted when the test finishes.
/// </summary>
/// <remarks>
/// Deliberately not an in-memory database. Migrations, WAL, VACUUM and the read-only
/// open path all behave differently, or do not exist at all, against <c>:memory:</c>,
/// and those are exactly the things these tests are here to check.
/// </remarks>
internal sealed class TempDatabase : IDisposable
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "altim-storage-tests");

    private readonly string _directory;
    private AltimDatabase? _open;

    public TempDatabase()
    {
        _directory = Path.Combine(Root, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        FilePath = Path.Combine(_directory, AltimDatabase.FileName);
    }

    /// <summary>
    /// Where the database file is, whether or not it exists yet.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Opens the database, closing any connection this helper already had open.
    /// </summary>
    public AltimDatabase Open()
    {
        Close();
        _open = AltimDatabase.Open(FilePath);
        return _open;
    }

    /// <summary>
    /// Closes the database, leaving the file in place.
    /// </summary>
    public void Close()
    {
        _open?.Dispose();
        _open = null;
    }

    /// <summary>
    /// Runs statements against the file on a connection of its own, which is how a test
    /// arranges a state the public API will not produce.
    /// </summary>
    public void Execute(string sql)
    {
        using SqliteConnection connection = Connect();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads a single value from the file on a connection of its own.
    /// </summary>
    public object? Scalar(string sql)
    {
        using SqliteConnection connection = Connect();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// Reads the first column of every row.
    /// </summary>
    public List<string> QueryStrings(string sql)
    {
        List<string> values = [];

        using SqliteConnection connection = Connect();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>
    /// Reads the first two columns of every row.
    /// </summary>
    public List<(string First, string Second)> QueryPairs(string sql)
    {
        List<(string First, string Second)> values = [];

        using SqliteConnection connection = Connect();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add((reader.GetString(0), reader.GetString(1)));
        }

        return values;
    }

    /// <summary>
    /// Reads a single integer value, failing the test if the column was null.
    /// </summary>
    public long ScalarInt64(string sql) => Assert.IsType<long>(Scalar(sql));

    /// <summary>
    /// Reads a single real value, failing the test if the column was null.
    /// </summary>
    public double ScalarDouble(string sql) => Assert.IsType<double>(Scalar(sql));

    /// <summary>
    /// Counts the rows in a table.
    /// </summary>
    public long CountRows(string table) => ScalarInt64($"SELECT count(*) FROM {table}");

    /// <summary>
    /// Closes the database and deletes its directory.
    /// </summary>
    /// <exception cref="IOException">
    /// The directory could not be removed, which on Windows means something still holds
    /// a handle to the file. That is a leaked connection in the code under test, and it
    /// fails the test that leaked it: swallowing it here is how a handle leak survives a
    /// green suite forever.
    /// </exception>
    public void Dispose()
    {
        Close();

        Delete(_directory);

        // The shared parent is emptied by whichever test finishes last; the others find
        // it still occupied, which is not their problem.
        try
        {
            Directory.Delete(Root);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Deletes the directory, retrying briefly. Windows releases a closed file handle
    /// asynchronously often enough that one attempt would be flaky, and a retry loop
    /// that ends in a throw still fails on a genuine leak.
    /// </summary>
    private static void Delete(string directory)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception error) when (attempt < 10 && error is IOException
                                          or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private SqliteConnection Connect()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }
}
