using Altim.Core.Models;

namespace Altim.Core.Abstractions;

/// <summary>
/// A provider that can report usage from before Altim was watching.
/// </summary>
/// <remarks>
/// Optional: a provider that cannot reach into the past simply does not implement this, and the
/// days it cannot account for stay unknown rather than being shown as zero.
/// </remarks>
public interface IUsageHistorySource
{
    /// <summary>
    /// Reads whole days from the provider's own history, oldest first.
    /// </summary>
    /// <param name="from">First day to read, inclusive.</param>
    /// <param name="to">Last day to read, inclusive.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// One entry per day the provider could account for. A day it cannot account for is absent
    /// from the result rather than present with zeroes.
    /// </returns>
    ValueTask<IReadOnlyList<UsageDay>> GetHistoryAsync(DateOnly from, DateOnly to, CancellationToken ct);
}
