using System.Data.Common;
using System.Globalization;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Usage;
using Microsoft.Data.Sqlite;

namespace Altim.Storage;

/// <summary>
/// The durable usage history, in the <c>usage_sample</c> table.
/// </summary>
/// <remarks>
/// <para>
/// A reading is stored only when it differs from the last row for that provider and
/// metric, so an idle machine adds nothing to the file for as long as it stays idle.
/// That is what keeps the database inside its size budget without a background job.
/// It also means an empty range is not an empty history: see
/// <see cref="GetLatestBeforeAsync"/>.
/// </para>
/// <para>
/// Unknown stays unknown: a metric the provider did not report, or reported
/// implausibly, is written as NULL and read back as <see langword="null"/>, never as
/// zero. Nothing but numbers, timestamps and the provider and metric identifiers is
/// written; no project name, prompt, command or path reaches this layer at all.
/// </para>
/// <para>
/// Microsoft.Data.Sqlite runs every statement inline, so an <c>async</c> signature here
/// buys nothing by itself. The two read methods and their callers can be looking at
/// thirty days of rows, so they are moved onto a worker; <see cref="RecordAsync"/> is a
/// handful of single-row statements and stays on the calling thread.
/// </para>
/// </remarks>
public sealed class SqliteUsageHistoryService : IUsageHistoryService
{
    private const string SampleColumns =
        "provider_id, metric_key, captured_at, used_percent, window_minutes, resets_at, "
        + "input_tokens, output_tokens, cache_read_tokens, cache_write_tokens";

    private const string DayColumns =
        "provider_id, day, input_tokens, output_tokens, cache_read_tokens, "
        + "cache_write_tokens, peak_percent, source, updated_at";

    /// <summary>The value of <c>usage_day.source</c> for a day Altim watched itself.</summary>
    private const string ObservedSource = "observed";

    /// <summary>The value of <c>usage_day.source</c> for a day read from a provider's history.</summary>
    private const string BackfilledSource = "backfilled";

    /// <summary>
    /// Whether the day being written raises the stored peak. Written once and used twice —
    /// in the conflict clause's assignment and in its <c>WHERE</c> — because the two have to
    /// agree exactly: a condition that writes and a condition that decides the row changed
    /// must be the same condition, or the stamp moves for a write that moved nothing.
    /// </summary>
    /// <remarks>
    /// A peak arriving where there was none is a rise. <c>NULL &gt; anything</c> and
    /// <c>anything &gt; NULL</c> are both NULL, which a <c>WHERE</c> reads as false, so the
    /// null case is spelled out rather than left to the comparison.
    /// </remarks>
    private const string PeakRises =
        "(excluded.peak_percent IS NOT NULL "
        + "AND (usage_day.peak_percent IS NULL OR excluded.peak_percent > usage_day.peak_percent))";

    /// <summary>
    /// A day, for widening a read window past the furthest any timezone can be from UTC.
    /// Not a unit of local time: a local day that crosses a DST boundary is not this long,
    /// which is exactly why the day a sample falls on is decided by a calendar and not by
    /// arithmetic.
    /// </summary>
    private const long SecondsPerDay = 24 * 60 * 60;

    private readonly AltimDatabase _database;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the service over an open database, timed by the system clock.
    /// </summary>
    /// <param name="database">The open database.</param>
    public SqliteUsageHistoryService(AltimDatabase database)
        : this(database, TimeProvider.System)
    {
    }

    /// <summary>
    /// Creates the service over an open database with an explicit clock, which is how
    /// the tests drive it.
    /// </summary>
    /// <param name="database">The open database.</param>
    /// <param name="timeProvider">
    /// Supplies the capture instant for a reading that does not carry one of its own.
    /// </param>
    public SqliteUsageHistoryService(AltimDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _database = database;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A reading in <see cref="ProviderStatus.Error"/> is not recorded at all. Its
    /// metrics are unavailable rather than zero or null-because-measured, and writing
    /// them would turn a transient read failure into a hole in the history that later
    /// reads cannot tell apart from a genuine drop to nothing. This matches what
    /// <c>UsageAggregator</c> and <c>ThresholdEvaluator</c> do with the same reading.
    /// </para>
    /// <para>
    /// Runs on the calling thread: one short lookup and at most one insert per metric,
    /// inside one transaction. It is called from the monitoring scheduler, which is
    /// already off the UI thread.
    /// </para>
    /// </remarks>
    public async ValueTask RecordAsync(ProviderUsage usage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (usage.Status == ProviderStatus.Error || usage.Metrics.Count == 0)
        {
            return;
        }

        long capturedAt = (usage.LastRefreshed ?? _timeProvider.GetUtcNow()).ToUnixTimeSeconds();

        // Asked on a read connection, before the writer is taken. Almost every reading
        // leaves every metric where it was, and the comparison below would then open a
        // write transaction, find nothing to do and commit nothing — a transaction, a
        // commit and a queue behind the single writer, all to discover that history has
        // not moved. The read is the cheap half of the same question and it blocks
        // nobody.
        if (!await HasChangesAsync(usage, ct).ConfigureAwait(false))
        {
            return;
        }

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (UsageMetric metric in usage.Metrics)
        {
            // Known and deliberately not fixed yet: the token totals belong to the
            // reading, not to the metric, so an hour in which one metric moves writes the
            // same token counts onto every metric's row. Revisit once real growth has
            // been measured against the 5MB/year budget; splitting them into their own
            // table is a migration, not a tidy-up.
            SampleValues candidate = ToValues(metric, usage.Tokens);
            SampleValues? previous = await ReadLatestAsync(connection, usage.ProviderId, metric.Key, ct)
                .ConfigureAwait(false);

            // A value that has not moved is not history, it is noise. Float comparison is
            // exact on purpose: both sides came from the same provider reading and went
            // through SQLite's REAL column, which round-trips an IEEE754 double unchanged.
            if (previous == candidate)
            {
                continue;
            }

            await InsertAsync(connection, usage.ProviderId, metric.Key, capturedAt, candidate, ct)
                .ConfigureAwait(false);
        }

        transaction.Commit();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on a worker: a 30-day range is the case this exists for, and the dashboard
    /// asks for one from the UI thread.
    /// </remarks>
    public async ValueTask<IReadOnlyList<UsageSample>> GetRangeAsync(
        string providerId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);

        long fromSeconds = from.ToUnixTimeSeconds();
        long toSeconds = to.ToUnixTimeSeconds();

        return await Task.Run(() => ReadRange(providerId, fromSeconds, toSeconds, ct), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on a worker. The lookup is one row per metric, but it reaches back past the
    /// retention boundary on a file that may hold a year of history.
    /// </remarks>
    public async ValueTask<IReadOnlyList<UsageSample>> GetLatestBeforeAsync(
        string providerId, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);

        long atSeconds = at.ToUnixTimeSeconds();

        return await Task.Run(() => ReadLatestBefore(providerId, atSeconds, ct), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on a worker: the map asks for a year at a time, from the UI thread, and
    /// Microsoft.Data.Sqlite would read all 730 rows on whatever thread asked.
    /// </remarks>
    public async ValueTask<IReadOnlyList<UsageDay>> GetDaysAsync(string providerId, DateOnly from,
                                                                  DateOnly to, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);

        // ISO text sorts and compares chronologically, so the range is the same
        // comparison in SQLite that it is in C#.
        string fromDay = FormatDay(from);
        string toDay = FormatDay(to);

        return await Task.Run(() => ReadDays(providerId, fromDay, toDay, ct), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Runs on the calling thread, behind the write gate, with every day in one
    /// transaction: a backfill hands over a month at a time and either all of it lands
    /// or none of it does.
    /// </para>
    /// <para>
    /// Nothing to write takes no writer at all. Holding the lease stamps the write clock,
    /// which is what <see cref="AltimDatabase.CheckpointIfIdleAsync"/> watches, so a
    /// caller that turns out to have no days would otherwise keep the WAL from ever being
    /// truncated by asking a question.
    /// </para>
    /// </remarks>
    public async ValueTask UpsertDaysAsync(IReadOnlyList<UsageDay> days, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(days);

        if (days.Count == 0)
        {
            return;
        }

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (UsageDay day in days)
        {
            await UpsertDayAsync(connection, day, ct).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// An empty range — a <paramref name="to"/> before <paramref name="from"/> — reads and
    /// writes nothing and returns zero, the same answer <see cref="GetDaysAsync"/> gives
    /// for the same bounds.
    /// </para>
    /// <para>
    /// The read runs on a worker and the write behind the writer lease, taken once for the
    /// whole range. Nothing holds the writer across the read: a sample landing in between is
    /// simply picked up by the next pass, and because the figures only ever rise it cannot
    /// make a row wrong, only briefly out of date.
    /// </para>
    /// </remarks>
    public async ValueTask<int> RollUpDaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from > to)
        {
            return 0;
        }

        // Read once, here, rather than per sample: the zone is a property the host may
        // change under us, and half a range bucketed by one zone and half by another would
        // be worse than either.
        TimeZoneInfo zone = _timeProvider.LocalTimeZone;
        DateTimeOffset updatedAt = _timeProvider.GetUtcNow();

        IReadOnlyList<UsageDay> days = await Task
            .Run(() => RollUp(from, to, zone, updatedAt, ct), ct)
            .ConfigureAwait(false);

        // Precedence is UpsertDaysAsync's single statement, not a decision repeated here;
        // and an empty range reaches its early return, so a rollup that found nothing never
        // takes the writer and never stamps the write clock the WAL checkpoint watches.
        await UpsertDaysAsync(days, ct).ConfigureAwait(false);

        return days.Count;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on the calling thread: two statements in one transaction, behind the write
    /// gate. Both tables are history, so clearing one without the other would leave a map
    /// drawn from days whose samples are gone. Reclaiming the space it frees is
    /// <see cref="UsageRetention.VacuumAsync"/>, which does not run here.
    /// </remarks>
    public async ValueTask ClearAsync(CancellationToken ct)
    {
        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand command = connection.CreateCommand())
        {
            // History only. Settings and notification state are not history and survive this.
            command.CommandText = """
                DELETE FROM usage_sample;
                DELETE FROM usage_day;
                """;
            _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private IReadOnlyList<UsageSample> ReadRange(string providerId, long from, long to,
                                                 CancellationToken ct)
    {
        using SqliteConnection connection = _database.OpenRead();
        using SqliteCommand command = connection.CreateCommand();

        // Half open: the lower bound is included, the upper bound is not, so adjacent
        // ranges neither overlap nor drop a sample between them.
        command.CommandText = $"""
            SELECT {SampleColumns}
            FROM usage_sample
            WHERE provider_id = $provider AND captured_at >= $from AND captured_at < $to
            ORDER BY captured_at, id
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        return ReadSamples(command, ct);
    }

    private IReadOnlyList<UsageSample> ReadLatestBefore(string providerId, long at,
                                                        CancellationToken ct)
    {
        using SqliteConnection connection = _database.OpenRead();
        using SqliteCommand command = connection.CreateCommand();

        // Strictly before, and one row per metric: the newest row wins, with the larger
        // id breaking a tie between two rows stamped the same second, which is the same
        // order the "has this moved?" lookup uses.
        command.CommandText = $"""
            SELECT {SampleColumns}
            FROM (
                SELECT {SampleColumns},
                       ROW_NUMBER() OVER (PARTITION BY metric_key
                                          ORDER BY captured_at DESC, id DESC) AS recency
                FROM usage_sample
                WHERE provider_id = $provider AND captured_at < $at
            )
            WHERE recency = 1
            ORDER BY metric_key
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$at", at);

        return ReadSamples(command, ct);
    }

    private IReadOnlyList<UsageDay> ReadDays(string providerId, string from, string to,
                                             CancellationToken ct)
    {
        using SqliteConnection connection = _database.OpenRead();
        using SqliteCommand command = connection.CreateCommand();

        // Both bounds inclusive, unlike GetRangeAsync: a day is a whole unit and the
        // caller names the last one it wants rather than the first one it does not.
        command.CommandText = $"""
            SELECT {DayColumns}
            FROM usage_day
            WHERE provider_id = $provider AND day >= $from AND day <= $to
            ORDER BY day
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        List<UsageDay> days = [];

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            days.Add(ReadDay(reader));
        }

        return days;
    }

    /// <summary>
    /// Reads every provider's samples around a range of local days and collapses each
    /// provider's day into its row.
    /// </summary>
    /// <param name="from">First local day wanted, inclusive.</param>
    /// <param name="to">Last local day wanted, inclusive.</param>
    /// <param name="zone">The zone whose calendar decides which day a sample fell on.</param>
    /// <param name="updatedAt">The stamp to put on every row produced.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// One entry per provider and day that reported anything. A day that reported nothing
    /// is absent rather than present and empty.
    /// </returns>
    private List<UsageDay> RollUp(DateOnly from, DateOnly to, TimeZoneInfo zone,
                                  DateTimeOffset updatedAt, CancellationToken ct)
    {
        // Read a day wide at each end, then decide the day in C#. No zone is a whole day
        // from UTC, so this cannot miss a sample; and asking SQLite instead is not an
        // option, because it has no timezone database and the spec fixes the day as the
        // user's local one. Widening also sidesteps converting a local midnight, which in
        // some zones is an hour a DST jump skipped and which has no UTC instant at all.
        long fromSeconds = StartOfUtcDay(from) - SecondsPerDay;
        long toSeconds = StartOfUtcDay(to) + (2 * SecondsPerDay);

        // Ordered for a stable, repeatable read. Nothing here depends on it: every figure
        // the rollup keeps is a maximum, so no order can change the answer.
        IReadOnlyList<UsageSample> samples = ReadAllProviders(fromSeconds, toSeconds, ct);

        Dictionary<(string ProviderId, DateOnly Day), List<UsageSample>> grouped = [];

        foreach (UsageSample sample in samples)
        {
            ct.ThrowIfCancellationRequested();

            DateOnly day = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(sample.CapturedAt, zone).DateTime);

            // The day either side that was read to be safe, and nothing else.
            if (day < from || day > to)
            {
                continue;
            }

            // By provider and day, never by day alone: two providers' figures are two
            // different quantities, and their percentages are measured against two
            // different limits.
            (string ProviderId, DateOnly Day) key = (sample.ProviderId, day);
            if (!grouped.TryGetValue(key, out List<UsageSample>? forDay))
            {
                forDay = [];
                grouped[key] = forDay;
            }

            forDay.Add(sample);
        }

        List<UsageDay> days = [];

        foreach (KeyValuePair<(string ProviderId, DateOnly Day), List<UsageSample>> group in grouped)
        {
            if (UsageDayRollup.FromSamples(group.Key.ProviderId, group.Key.Day, group.Value,
                                           updatedAt) is { } day)
            {
                days.Add(day);
            }
        }

        return days;
    }

    /// <summary>
    /// Every provider's samples in an instant range, oldest first. The rollup is the one
    /// read that wants all of them at once, because it writes a row per provider it finds
    /// rather than per provider it was asked about.
    /// </summary>
    private IReadOnlyList<UsageSample> ReadAllProviders(long from, long to, CancellationToken ct)
    {
        using SqliteConnection connection = _database.OpenRead();
        using SqliteCommand command = connection.CreateCommand();

        // Half open, as GetRangeAsync is: the bounds are instants here, not days.
        command.CommandText = $"""
            SELECT {SampleColumns}
            FROM usage_sample
            WHERE captured_at >= $from AND captured_at < $to
            ORDER BY captured_at, id
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        return ReadSamples(command, ct);
    }

    /// <summary>
    /// Midnight UTC on a day, in Unix seconds. Only ever a reference point for widening a
    /// read window, never the day a sample is filed under.
    /// </summary>
    private static long StartOfUtcDay(DateOnly day)
        => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>
    /// Inserts a day, or merges into the stored one field by field where the incoming row
    /// has something to say and it would actually change something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row has two writers that never overlap, and neither owns the whole of it.</b>
    /// The backfill owns the four token columns and <c>source</c>, because only a per-day
    /// source can say what a day spent; the sample rollup owns <c>peak_percent</c>, because
    /// only Altim's own readings measured the live windows. So a write that carries no
    /// tokens leaves the tokens and the source exactly as they were, and a write that
    /// carries no peak leaves the peak alone.
    /// </para>
    /// <para>
    /// The rule this replaced compared whole rows — observed beat backfilled — and it is
    /// retired because it was the mechanism of a real defect. The rollup, which carries no
    /// tokens at all, ranked above the backfill, so it wrote emptiness over per-day figures
    /// the provider had just supplied. Merging per field removes the ranking entirely:
    /// there is nothing for the two writers to compete over.
    /// </para>
    /// <para>
    /// <b>A peak may only ever rise.</b> It is the highest the day reached, so a later pass
    /// that measures the window quieter is describing a moment, not the day, and must not
    /// lower it. A peak arriving where there was none counts as a rise, which is why the
    /// test is not a bare <c>&gt;</c>: comparing anything with NULL answers neither yes nor
    /// no, and that reads as no.
    /// </para>
    /// <para>
    /// The last clause of the <c>WHERE</c> is what keeps a quiet machine quiet. The
    /// maintenance pass rolls yesterday and today up on every tick, so a computer left on
    /// overnight offers the same unchanged day over and over. Accepting each one would move
    /// SQLite's change counter, and that counter is exactly what
    /// <see cref="AltimDatabase.CheckpointIfIdleAsync"/> reads to decide the database has
    /// been written to: the write-ahead log would then never be emptied again, by
    /// construction, which is the trap <c>ReleaseWriter</c> documents from the other end. A
    /// row nothing in this write would move is left alone, stamp included, so
    /// <c>updated_at</c> means "when this day last moved" rather than "when something last
    /// asked about it".
    /// </para>
    /// <para>
    /// <c>IS NOT</c> rather than <c>&lt;&gt;</c> on the token columns and the source,
    /// because every one of them is nullable and an unreported component must compare equal
    /// to an unreported component rather than to nothing at all. A figure appearing where
    /// there was none is a change, and <c>&lt;&gt;</c> would report it as no change at all.
    /// </para>
    /// </remarks>
    private static async ValueTask UpsertDayAsync(SqliteConnection connection, UsageDay day,
                                                  CancellationToken ct)
    {
        // Whether this write has any standing over the token columns at all. Asked once here
        // rather than four times in SQL, and it is "any component reported", not "all four":
        // a source that reports input and nothing else still knows the day.
        bool carriesTokens = day.Tokens is { } totals
            && (totals.Input is not null || totals.Output is not null
                || totals.CacheRead is not null || totals.CacheWrite is not null);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO usage_day ({DayColumns})
            VALUES ($provider, $day, $input, $output, $cacheRead, $cacheWrite, $peak,
                    $source, $updatedAt)
            ON CONFLICT (provider_id, day) DO UPDATE SET
              input_tokens       = CASE WHEN $carriesTokens THEN excluded.input_tokens
                                        ELSE usage_day.input_tokens END,
              output_tokens      = CASE WHEN $carriesTokens THEN excluded.output_tokens
                                        ELSE usage_day.output_tokens END,
              cache_read_tokens  = CASE WHEN $carriesTokens THEN excluded.cache_read_tokens
                                        ELSE usage_day.cache_read_tokens END,
              cache_write_tokens = CASE WHEN $carriesTokens THEN excluded.cache_write_tokens
                                        ELSE usage_day.cache_write_tokens END,
              source             = CASE WHEN $carriesTokens THEN excluded.source
                                        ELSE usage_day.source END,
              peak_percent       = CASE WHEN {PeakRises} THEN excluded.peak_percent
                                        ELSE usage_day.peak_percent END,
              updated_at         = excluded.updated_at
            WHERE ($carriesTokens
                   AND (usage_day.input_tokens       IS NOT excluded.input_tokens
                     OR usage_day.output_tokens      IS NOT excluded.output_tokens
                     OR usage_day.cache_read_tokens  IS NOT excluded.cache_read_tokens
                     OR usage_day.cache_write_tokens IS NOT excluded.cache_write_tokens
                     OR usage_day.source             IS NOT excluded.source))
               OR {PeakRises}
            """;
        command.Parameters.AddWithValue("$provider", day.ProviderId);
        command.Parameters.AddWithValue("$day", FormatDay(day.Day));
        AddNullable(command, "$input", day.Tokens?.Input);
        AddNullable(command, "$output", day.Tokens?.Output);
        AddNullable(command, "$cacheRead", day.Tokens?.CacheRead);
        AddNullable(command, "$cacheWrite", day.Tokens?.CacheWrite);
        AddNullable(command, "$peak", day.PeakPercent);
        command.Parameters.AddWithValue("$source", SourceName(day.Source));
        command.Parameters.AddWithValue("$updatedAt", day.UpdatedAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$carriesTokens", carriesTokens ? 1 : 0);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static UsageDay ReadDay(DbDataReader reader)
    {
        long? input = NullableInt64(reader, 2);
        long? output = NullableInt64(reader, 3);
        long? cacheRead = NullableInt64(reader, 4);
        long? cacheWrite = NullableInt64(reader, 5);

        // All four unreported is a day that reported no tokens at all, which is not a day
        // that reported four zeroes.
        TokenTotals? tokens = input is null && output is null && cacheRead is null && cacheWrite is null
            ? null
            : new TokenTotals(input, output, cacheRead, cacheWrite);

        return new UsageDay(
            reader.GetString(0),
            ParseDay(reader.GetString(1)),
            tokens,
            NullableDouble(reader, 6),
            ParseSource(reader.GetString(7)),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)));
    }

    private static string FormatDay(DateOnly day)
        => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string day)
        => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string SourceName(UsageDaySource source) => source switch
    {
        UsageDaySource.Observed => ObservedSource,
        UsageDaySource.Backfilled => BackfilledSource,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source,
                                                   "Unknown usage day source."),
    };

    /// <summary>
    /// Reads the stored source, treating anything unrecognised as backfilled.
    /// </summary>
    /// <remarks>
    /// Only this class writes the column, so an unrecognised value means the file was
    /// edited by hand. Backfilled is the honest answer to that: claiming a day was
    /// observed would assert that Altim watched it, and would also make the row
    /// unreplaceable by the rollup that could put it right.
    /// </remarks>
    private static UsageDaySource ParseSource(string source)
        => string.Equals(source, ObservedSource, StringComparison.Ordinal)
            ? UsageDaySource.Observed
            : UsageDaySource.Backfilled;

    private static List<UsageSample> ReadSamples(SqliteCommand command, CancellationToken ct)
    {
        List<UsageSample> samples = [];

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            samples.Add(ReadSample(reader));
        }

        return samples;
    }

    private static SampleValues ToValues(UsageMetric metric, TokenTotals? tokens)
    {
        // ReportedPercent, not the raw value: a provider defect can report 101, and history
        // must never store a figure the UI would refuse to render.
        double? usedPercent = metric.ReportedPercent;
        long? windowMinutes = metric.Window is null
            ? null
            : (long)Math.Round(metric.Window.Length.TotalMinutes, MidpointRounding.AwayFromZero);

        return new SampleValues(
            usedPercent,
            windowMinutes,
            metric.Window?.ResetsAt?.ToUnixTimeSeconds(),
            tokens?.Input,
            tokens?.Output,
            tokens?.CacheRead,
            tokens?.CacheWrite);
    }

    /// <summary>
    /// Whether any metric in a reading differs from the last row recorded for it.
    /// </summary>
    /// <param name="usage">The reading about to be recorded.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <remarks>
    /// The same comparison the write path makes, on a connection that cannot write. It is
    /// a pre-check rather than the decision: the write path compares again inside its own
    /// transaction, so a value that changes between the two is still handled correctly and
    /// this can only ever cost an unnecessary transaction, never a lost sample.
    /// </remarks>
    private async ValueTask<bool> HasChangesAsync(ProviderUsage usage, CancellationToken ct)
    {
        using SqliteConnection connection = _database.OpenRead();

        foreach (UsageMetric metric in usage.Metrics)
        {
            SampleValues candidate = ToValues(metric, usage.Tokens);
            SampleValues? previous = await ReadLatestAsync(connection, usage.ProviderId, metric.Key, ct)
                .ConfigureAwait(false);

            if (previous != candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<SampleValues?> ReadLatestAsync(
        SqliteConnection connection, string providerId, string metricKey, CancellationToken ct)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT used_percent, window_minutes, resets_at,
                   input_tokens, output_tokens, cache_read_tokens, cache_write_tokens
            FROM usage_sample
            WHERE provider_id = $provider AND metric_key = $metric
            ORDER BY captured_at DESC, id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$metric", metricKey);

        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SampleValues(
            NullableDouble(reader, 0),
            NullableInt64(reader, 1),
            NullableInt64(reader, 2),
            NullableInt64(reader, 3),
            NullableInt64(reader, 4),
            NullableInt64(reader, 5),
            NullableInt64(reader, 6));
    }

    private static async ValueTask InsertAsync(SqliteConnection connection, string providerId,
                                                string metricKey, long capturedAt,
                                                SampleValues values, CancellationToken ct)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO usage_sample ({SampleColumns})
            VALUES ($provider, $metric, $captured, $percent, $window, $resets,
                    $input, $output, $cacheRead, $cacheWrite)
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$metric", metricKey);
        command.Parameters.AddWithValue("$captured", capturedAt);
        AddNullable(command, "$percent", values.UsedPercent);
        AddNullable(command, "$window", values.WindowMinutes);
        AddNullable(command, "$resets", values.ResetsAt);
        AddNullable(command, "$input", values.InputTokens);
        AddNullable(command, "$output", values.OutputTokens);
        AddNullable(command, "$cacheRead", values.CacheReadTokens);
        AddNullable(command, "$cacheWrite", values.CacheWriteTokens);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static UsageSample ReadSample(DbDataReader reader)
    {
        long? windowMinutes = NullableInt64(reader, 4);
        long? resetsAt = NullableInt64(reader, 5);

        long? input = NullableInt64(reader, 6);
        long? output = NullableInt64(reader, 7);
        long? cacheRead = NullableInt64(reader, 8);
        long? cacheWrite = NullableInt64(reader, 9);

        TokenTotals? tokens = input is null && output is null && cacheRead is null && cacheWrite is null
            ? null
            : new TokenTotals(input, output, cacheRead, cacheWrite);

        return new UsageSample(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
            NullableDouble(reader, 3),
            windowMinutes is null ? null : TimeSpan.FromMinutes(windowMinutes.Value),
            resetsAt is null ? null : DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value),
            tokens);
    }

    private static void AddNullable(SqliteCommand command, string name, double? value)
        => command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private static void AddNullable(SqliteCommand command, string name, long? value)
        => command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private static double? NullableDouble(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static long? NullableInt64(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    /// <summary>
    /// Everything about a sample except which provider, metric and instant it belongs
    /// to. Comparing two of these is the whole of the "has anything changed?" decision,
    /// and a record struct gives that comparison for free.
    /// </summary>
    private readonly record struct SampleValues(
        double? UsedPercent,
        long? WindowMinutes,
        long? ResetsAt,
        long? InputTokens,
        long? OutputTokens,
        long? CacheReadTokens,
        long? CacheWriteTokens);
}
