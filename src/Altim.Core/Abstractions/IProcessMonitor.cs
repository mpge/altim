using Altim.Core.Models;

namespace Altim.Core.Abstractions;

/// <summary>
/// Finds agent processes that are running right now, so a provider can say whether it
/// is active without reading its session store again.
/// </summary>
public interface IProcessMonitor
{
    /// <summary>
    /// Scans the process table once.
    /// </summary>
    /// <param name="ct">Cancels the scan.</param>
    /// <returns>
    /// The matching processes, or an empty list when none are running. An empty list
    /// means nothing matched, and never that the scan failed: a scan that cannot read
    /// the process table also returns empty rather than throwing, because a denied
    /// query is the normal case for processes owned by another user.
    /// </returns>
    ValueTask<IReadOnlyList<DetectedProcess>> ScanAsync(CancellationToken ct);
}
