using Microsoft.Data.Sqlite;
using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// The schema has to apply cleanly and has to keep unknown values unknown. Both are
/// checked against a real SQLite engine rather than against the script text.
/// </summary>
public sealed class SqliteSchemaTests
{
    [Fact]
    public void CreateScriptAppliesToAnEmptyDatabase()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Execute(connection, SqliteSchema.CreateScript);

        List<string> tables = ObjectNames(connection, "table");

        Assert.Contains("schema_version", tables);
        Assert.Contains("usage_sample", tables);
        Assert.Contains("setting", tables);
        Assert.Contains("notification_state", tables);
        Assert.Contains("ix_usage_sample_lookup", ObjectNames(connection, "index"));
    }

    [Fact]
    public void UnknownPercentRoundTripsAsNullNotZero()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, SqliteSchema.CreateScript);

        Execute(connection, """
            INSERT INTO usage_sample (provider_id, metric_key, captured_at, used_percent)
            VALUES ('claude', 'five_hour', 1763000000, NULL);
            """);

        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT used_percent FROM usage_sample";
        object? value = read.ExecuteScalar();

        Assert.Equal(DBNull.Value, value);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> ObjectNames(SqliteConnection connection, string type)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type";
        command.Parameters.AddWithValue("$type", type);

        List<string> names = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
