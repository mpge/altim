using Altim.Core.Models;

namespace Altim.Core.Abstractions;

/// <summary>
/// The durable record of past readings. Samples are written only when a value
/// changes, and are down-sampled after 30 days to hourly rows.
/// </summary>
public interface IUsageHistoryService
{
    /// <summary>
    /// Records a reading. A metric whose value has not moved since the last stored
    /// sample is skipped, and a metric with no reported value is stored with a null
    /// percentage rather than a zero.
    /// </summary>
    /// <param name="usage">The reading to record.</param>
    /// <param name="ct">Cancels the write.</param>
    ValueTask RecordAsync(ProviderUsage usage, CancellationToken ct);

    /// <summary>
    /// Returns every sample for one provider inside a half-open interval, oldest
    /// first. An empty list means nothing <em>changed</em> in that interval, which is
    /// not the same as nothing being known: see
    /// <see cref="GetLatestBeforeAsync"/> for the value carried into the interval.
    /// Nothing here is ever rendered as a flat line at zero.
    /// </summary>
    /// <param name="providerId">The provider to read.</param>
    /// <param name="from">Inclusive lower bound.</param>
    /// <param name="to">Exclusive upper bound.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<IReadOnlyList<UsageSample>> GetRangeAsync(string providerId, DateTimeOffset from,
                                                        DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Returns the most recent sample of each metric of one provider strictly before
    /// an instant: the value that was still standing when that instant arrived.
    /// </summary>
    /// <param name="providerId">The provider to read.</param>
    /// <param name="at">
    /// The exclusive upper bound. A sample captured exactly at this instant belongs to
    /// the range that starts here, not to the carry-in, which is what keeps this
    /// method and <see cref="GetRangeAsync"/> from reporting the same row twice.
    /// </param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// At most one sample per <see cref="UsageMetric.Key"/>, ordered by metric key.
    /// Empty when the provider has no history at all before <paramref name="at"/>,
    /// which is the only case that really means "nothing was ever recorded".
    /// </returns>
    /// <remarks>
    /// <para>
    /// Samples are written only when a value changes, so a quiet period writes no rows
    /// at all: a 24-hour chart of a machine whose last change was 25 hours ago gets an
    /// empty <see cref="GetRangeAsync"/> result even though the usage is perfectly
    /// well known. A chart caller therefore asks for both — this method for the
    /// carry-in value at the left edge of the window, and
    /// <see cref="GetRangeAsync"/> for everything inside it — and draws the carry-in
    /// as the series' value from the left edge until the first sample in range.
    /// </para>
    /// <para>
    /// "No usage recorded yet" belongs on the screen only when both come back empty.
    /// </para>
    /// </remarks>
    ValueTask<IReadOnlyList<UsageSample>> GetLatestBeforeAsync(string providerId, DateTimeOffset at,
                                                               CancellationToken ct);

    /// <summary>Reads stored days for one provider, oldest first, both bounds inclusive.</summary>
    /// <param name="providerId">The provider to read.</param>
    /// <param name="from">First day, inclusive.</param>
    /// <param name="to">Last day, inclusive.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>One entry per day that has a row. A day with no row is absent: it is unknown, not zero.</returns>
    ValueTask<IReadOnlyList<UsageDay>> GetDaysAsync(string providerId, DateOnly from, DateOnly to,
                                                    CancellationToken ct);

    /// <summary>
    /// Writes days, inserting or updating one row per provider and day.
    /// </summary>
    /// <param name="days">The days to write.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <remarks>
    /// <para>
    /// <b>A write merges into the stored row field by field; it does not replace it.</b> Two
    /// writers share a row and neither owns the whole of it. The four token components and
    /// <see cref="UsageDay.Source"/> belong to whichever write actually carries tokens —
    /// only a per-day source knows what a day spent — so a write carrying none leaves both
    /// alone. <see cref="UsageDay.PeakPercent"/> belongs to the write that measured the day
    /// highest: a peak may rise, may appear where there was none, and may never fall.
    /// </para>
    /// <para>
    /// There is deliberately no ranking between observed and backfilled rows. The rule that
    /// ranked them let a rollup, which has no token figure to offer, overwrite a correct
    /// per-day figure with nothing.
    /// </para>
    /// </remarks>
    ValueTask UpsertDaysAsync(IReadOnlyList<UsageDay> days, CancellationToken ct);

    /// <summary>
    /// Rolls the samples this service already holds up into one observed day row per
    /// provider per local calendar day, across a range with both bounds inclusive. Each row
    /// carries that day's <b>peak percentage and no token figure</b>.
    /// </summary>
    /// <param name="from">First local day to roll up, inclusive.</param>
    /// <param name="to">
    /// Last local day to roll up, inclusive. A <paramref name="to"/> before
    /// <paramref name="from"/> names no days at all, which is nothing to do rather than
    /// something to complain about.
    /// </param>
    /// <param name="ct">Cancels the read and the write.</param>
    /// <returns>
    /// How many day rows were written. A day whose samples reported no usable percentage
    /// rolls up to nothing, is not written, and is not counted: it stays unknown rather than
    /// becoming a row that says only that Altim was running.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>No row it writes carries a token figure.</b> A reading's token totals are a running
    /// total and never a per-day amount, so there is no arithmetic over them that yields a
    /// day; the day's tokens come only from a per-day source, through the backfill. See
    /// <see cref="Usage.UsageDayRollup"/> for the two provider mechanisms behind that.
    /// </para>
    /// <para>
    /// Every row it writes is <see cref="UsageDaySource.Observed"/> and its peak is the
    /// highest percentage any of that day's readings reported, so rolling a day up again
    /// produces the same row or a higher peak, never a lower one and never a second row.
    /// That is what makes it safe to include <em>today</em>, a day still being lived, and to
    /// run the whole thing over and over from a housekeeping pass.
    /// </para>
    /// <para>
    /// This is on the interface rather than on the one implementation that does the work,
    /// because the caller holds an <see cref="IUsageHistoryService"/> that degrades to a
    /// no-op when the database could not be opened. Type-testing for the concrete class at
    /// the call site would skip the rollup silently on that path — correct behaviour,
    /// arrived at invisibly, with no way to test it. A no-op that returns zero says the
    /// same thing out loud.
    /// </para>
    /// </remarks>
    ValueTask<int> RollUpDaysAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>
    /// Deletes all recorded history. Offered to the user as an explicit action; there
    /// is no automatic path that calls it.
    /// </summary>
    /// <param name="ct">Cancels the delete.</param>
    ValueTask ClearAsync(CancellationToken ct);
}
