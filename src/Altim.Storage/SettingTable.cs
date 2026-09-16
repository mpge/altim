using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Altim.Storage;

/// <summary>
/// The raw key/value access to the <c>setting</c> table, shared by everything that
/// keeps a small scalar there. Typing and defaulting live one layer up in
/// <see cref="SqliteSettingsStore"/>; this layer only moves strings.
/// </summary>
/// <remarks>
/// Commands are always created from the connection, never constructed directly, so they
/// inherit whatever transaction the connection has open. Every statement is
/// parameterised.
/// </remarks>
internal static class SettingTable
{
    /// <summary>
    /// The synchronous sibling of <see cref="ReadAsync"/>, for callers that are already
    /// running on a worker and would only be pretending by awaiting.
    /// </summary>
    internal static string? Read(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM setting WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);

        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// The synchronous sibling of <see cref="WriteAsync"/>.
    /// </summary>
    internal static void Write(SqliteConnection connection, string key, string value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setting (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);

        _ = command.ExecuteNonQuery();
    }

    internal static async ValueTask<string?> ReadAsync(SqliteConnection connection, string key,
                                                       CancellationToken ct)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM setting WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);

        object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value as string;
    }

    internal static async ValueTask<Dictionary<string, string>> ReadAllAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM setting";

        Dictionary<string, string> values = new(StringComparer.Ordinal);

        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    internal static async ValueTask WriteAsync(SqliteConnection connection, string key, string value,
                                                CancellationToken ct)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setting (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async ValueTask RemoveAsync(SqliteConnection connection, string key,
                                                 CancellationToken ct)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM setting WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
