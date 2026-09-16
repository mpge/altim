using Xunit;

namespace Altim.Storage.Tests;

/// <summary>
/// The privacy promise is a property of the schema, not of the code that happens to
/// write to it today. If a column exists that could hold a project name, a prompt, a
/// command or a path, something will eventually fill it, so the guard is on the column
/// list itself.
/// </summary>
public sealed class SchemaPrivacyTests
{
    /// <summary>
    /// Fragments that have no business in a schema holding numbers and timestamps. A new
    /// column tripping this list is a design question, not a test to relax.
    /// </summary>
    private static readonly string[] Forbidden =
    [
        "prompt", "message", "content", "text", "transcript", "conversation", "reply",
        "path", "file", "dir", "folder", "cwd", "workspace", "project", "repo", "branch",
        "command", "argv", "arg", "script", "url", "uri", "host", "email", "title", "body",
        "session", "user", "account", "summary", "label", "note",
    ];

    private const string AllColumns = """
        SELECT tables.name, columns.name
        FROM sqlite_master AS tables
        JOIN pragma_table_info(tables.name) AS columns
        WHERE tables.type = 'table' AND tables.name NOT LIKE 'sqlite_%'
        """;

    private const string AllIndexes =
        "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%'";

    private const string AllTables =
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";

    [Fact]
    public void NoColumnCanHoldAProjectNamePromptCommandOrPath()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();

        foreach ((string table, string column) in temp.QueryPairs(AllColumns))
        {
            foreach (string fragment in Forbidden)
            {
                Assert.False(
                    column.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"Column {table}.{column} looks like it could hold user content.");
            }
        }
    }

    [Fact]
    public void NoIndexNameCarriesUserContentEither()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();

        foreach (string index in temp.QueryStrings(AllIndexes))
        {
            foreach (string fragment in Forbidden)
            {
                Assert.False(
                    index.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"Index {index} looks like it could describe user content.");
            }
        }
    }

    [Fact]
    public void TheSchemaHoldsExactlyTheDocumentedTables()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();

        string[] expected = ["notification_state", "schema_version", "setting", "usage_sample"];

        Assert.Equal(expected, temp.QueryStrings(AllTables).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EveryUsageColumnIsANumberATimestampOrAnIdentifier()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();

        string[] expected =
        [
            "cache_read_tokens", "cache_write_tokens", "captured_at", "id", "input_tokens",
            "metric_key", "output_tokens", "provider_id", "resets_at", "used_percent",
            "window_minutes",
        ];

        string[] actual = temp.QueryPairs(AllColumns)
            .Where(c => c.First == "usage_sample")
            .Select(c => c.Second)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TheIndexIsTheLookupTheHistoryQueriesActuallyMake()
    {
        using var temp = new TempDatabase();
        _ = temp.Open();

        string[] expected = ["ix_usage_sample_lookup"];

        Assert.Equal(expected, temp.QueryStrings(AllIndexes).ToArray());
    }
}
