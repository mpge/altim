namespace Altim.Core.Models;

/// <summary>
/// Everything one provider reports at one instant. Deliberately open-ended, because
/// providers do not expose the same metrics as each other.
/// </summary>
/// <param name="ProviderId">
/// The reporting provider's stable identifier, for example <c>claude</c>.
/// </param>
/// <param name="Status">
/// Where the integration stands. <see cref="ProviderStatus.Error"/> means the reading
/// failed, and every metric on it is unavailable.
/// </param>
/// <param name="Metrics">
/// Whatever this provider actually reports. Empty means the provider reports no
/// metrics at all, which is a legitimate answer and not an error.
/// </param>
/// <param name="Tokens">
/// Token totals across the reported windows. <see langword="null"/> when the provider
/// does not report token counts.
/// </param>
/// <param name="LastRefreshed">
/// When this reading was taken. <see langword="null"/> when no successful read has
/// happened yet, so the UI shows no "last refreshed" line rather than an epoch.
/// </param>
/// <param name="StatusDetail">
/// Human-readable explanation of a non-normal status, for example
/// "Unable to retrieve usage". <see langword="null"/> when there is nothing to explain.
/// </param>
public sealed record ProviderUsage(
    string ProviderId,
    ProviderStatus Status,
    IReadOnlyList<UsageMetric> Metrics,
    TokenTotals? Tokens,
    DateTimeOffset? LastRefreshed,
    string? StatusDetail);
