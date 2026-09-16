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

    /// <summary>
    /// Deletes all recorded history. Offered to the user as an explicit action; there
    /// is no automatic path that calls it.
    /// </summary>
    /// <param name="ct">Cancels the delete.</param>
    ValueTask ClearAsync(CancellationToken ct);
}
