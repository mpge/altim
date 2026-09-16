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
    /// first. An empty list means nothing was recorded in that interval, which the UI
    /// renders as "No usage recorded yet" rather than as a flat line at zero.
    /// </summary>
    /// <param name="providerId">The provider to read.</param>
    /// <param name="from">Inclusive lower bound.</param>
    /// <param name="to">Exclusive upper bound.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<IReadOnlyList<UsageSample>> GetRangeAsync(string providerId, DateTimeOffset from,
                                                        DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Deletes all recorded history. Offered to the user as an explicit action; there
    /// is no automatic path that calls it.
    /// </summary>
    /// <param name="ct">Cancels the delete.</param>
    ValueTask ClearAsync(CancellationToken ct);
}
