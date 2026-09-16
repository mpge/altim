namespace Altim.Storage;

/// <summary>
/// The SQLite schema, verbatim. Migrations are sequential and forward only, each in
/// its own transaction, so this script is only ever run against an empty database;
/// every later change arrives as its own migration rather than an edit here.
/// </summary>
/// <remarks>
/// No project name, prompt, command or file path is stored. The nullable columns are
/// nullable on purpose: an unreported percentage, window or reset instant is written
/// as NULL and read back as null, never as zero.
/// </remarks>
public static class SqliteSchema
{
    /// <summary>
    /// The schema version this build expects. Compared against the single row of
    /// <c>schema_version</c> on open.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The statements that create version <see cref="CurrentVersion"/> of the schema.
    /// </summary>
    public const string CreateScript = """
        CREATE TABLE schema_version (version INTEGER NOT NULL);

        CREATE TABLE usage_sample (
          id INTEGER PRIMARY KEY,
          provider_id TEXT NOT NULL,
          metric_key  TEXT NOT NULL,
          captured_at INTEGER NOT NULL,          -- unix seconds, UTC
          used_percent REAL,                      -- nullable: unknown stays unknown
          window_minutes INTEGER,
          resets_at INTEGER,
          input_tokens INTEGER, output_tokens INTEGER,
          cache_read_tokens INTEGER, cache_write_tokens INTEGER
        );

        CREATE INDEX ix_usage_sample_lookup ON usage_sample (provider_id, metric_key, captured_at);

        CREATE TABLE setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE notification_state (
          provider_id TEXT NOT NULL, metric_key TEXT NOT NULL, threshold INTEGER NOT NULL,
          fired_at INTEGER NOT NULL, window_resets_at INTEGER,
          PRIMARY KEY (provider_id, metric_key, threshold)
        );
        """;
}
