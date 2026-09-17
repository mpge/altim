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
    public const int CurrentVersion = 3;

    /// <summary>
    /// The statements that create version 1 of the schema, which is the first rung of
    /// <see cref="Migrations"/> and is never edited. An empty file climbs the whole
    /// ladder from here, so a database created today still passes through version 1 on
    /// its way to <see cref="CurrentVersion"/>.
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

    /// <summary>
    /// The statements that take the schema from version 1 to version 2: one row per
    /// provider per local calendar day, which is what the usage map draws. It does not
    /// touch <c>usage_sample</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The day is text, not an instant, because it is the user's <em>local</em> calendar
    /// day resolved when the row is written. Storing an instant would let a timezone
    /// change silently re-bucket history that was already settled.
    /// </para>
    /// <para>
    /// The four token columns and <c>peak_percent</c> are nullable for the same reason
    /// the sample columns are: a day on which nothing was reported holds NULL and reads
    /// back as null, never as zero. A day nothing is known about has no row at all, and
    /// that absence is what the map draws as unknown.
    /// </para>
    /// </remarks>
    public const string CreateUsageDayScript = """
        CREATE TABLE usage_day (
          provider_id        TEXT    NOT NULL,
          day                TEXT    NOT NULL,   -- local calendar day, ISO yyyy-mm-dd
          input_tokens       INTEGER,
          output_tokens      INTEGER,
          cache_read_tokens  INTEGER,
          cache_write_tokens INTEGER,
          peak_percent       REAL,               -- nullable: unknown stays unknown
          source             TEXT    NOT NULL,   -- 'observed' | 'backfilled'
          updated_at         INTEGER NOT NULL,
          PRIMARY KEY (provider_id, day)
        );
        """;

    /// <summary>
    /// The statements that take the schema from version 2 to version 3: the token figures on
    /// every observed day are emptied, because they were never per-day amounts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rollup used to derive a day's tokens from Altim's own readings, and a reading's
    /// token totals are a running total: the Claude provider publishes the cumulative sum of
    /// every transcript its scanner has read, and the Codex provider publishes a sum over
    /// whichever sessions were most recently active, which moves in both directions between
    /// one read and the next. The rows written that way hold a counter's value as it stood at
    /// some moment of the day, and the map draws a row's tokens as that day's volume.
    /// </para>
    /// <para>
    /// A wrong figure is worse than no figure, and unknown is a square the map already draws
    /// honestly, so the columns are emptied rather than adjusted — there is no arithmetic
    /// that turns a running total back into a day. The peak is kept: it was always a real
    /// measurement of how close to the limit that day came. Backfilled rows came from the
    /// providers' own per-day history and are not touched.
    /// </para>
    /// <para>
    /// Forward-only and idempotent in effect: after it runs no observed row has a token
    /// figure, and the rollup no longer writes one, so nothing puts them back.
    /// </para>
    /// </remarks>
    public const string ClearObservedDayTokensScript = """
        UPDATE usage_day
        SET input_tokens = NULL,
            output_tokens = NULL,
            cache_read_tokens = NULL,
            cache_write_tokens = NULL
        WHERE source = 'observed';
        """;
}
