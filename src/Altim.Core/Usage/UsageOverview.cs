using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// Every provider reduced to the one line and the one meter the tray shows. Produced by
/// <see cref="UsageAggregator"/>, and nullable throughout, because an aggregate over
/// nothing is not zero.
/// </summary>
/// <param name="WorstProviderId">
/// The provider owning <paramref name="WorstMetric"/>. <see langword="null"/> when no
/// provider reported a usable percentage.
/// </param>
/// <param name="WorstMetric">
/// The metric closest to its limit across every provider, with its percentage already
/// normalised. <see langword="null"/> when nothing reported a usable percentage, which is
/// rendered as "Not reported by this provider" and never as zero. Metrics belonging to a
/// reading in <see cref="ProviderStatus.Error"/> are excluded, because a failed reading
/// carries no numbers.
/// </param>
/// <param name="Status">
/// The least healthy provider status present. <see cref="ProviderStatus.Unknown"/> when
/// there are no providers at all.
/// </param>
/// <param name="StatusLine">
/// One sentence for the tray and the panel header, for example "All providers
/// operational" or "claude unavailable". Never empty.
/// </param>
/// <param name="Tokens">
/// Component-wise sum of the token totals providers reported.
/// <see langword="null"/> when no provider reported any component at all, and individual
/// components stay <see langword="null"/> when nobody reported that component. A sum is
/// never seeded with zero.
/// </param>
/// <param name="ProviderCount">How many readings went into this overview.</param>
public sealed record UsageOverview(
    string? WorstProviderId,
    UsageMetric? WorstMetric,
    ProviderStatus Status,
    string StatusLine,
    TokenTotals? Tokens,
    int ProviderCount)
{
    /// <summary>
    /// The overview for no providers at all: nothing reported, nothing summed.
    /// </summary>
    public static UsageOverview Empty { get; } =
        new(null, null, ProviderStatus.Unknown, "No providers configured", null, 0);

    /// <summary>
    /// The highest normalised percentage any provider reported, or
    /// <see langword="null"/> when none did.
    /// </summary>
    public double? WorstUsedPercent => WorstMetric?.UsedPercent;
}
