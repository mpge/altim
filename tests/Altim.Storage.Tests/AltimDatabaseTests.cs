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
}
