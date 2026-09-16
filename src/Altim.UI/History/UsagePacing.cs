using Altim.Core.Models;
using Altim.Core.Usage;

namespace Altim.UI.History;

/// <summary>
/// Pacing: this window's level against the same point in the window before it.
/// </summary>
/// <remarks>
/// <para>
/// A percentage on its own says where you are. It does not say whether you are going to run
/// out, because that depends on how fast you got there. Pacing answers that with the only
/// comparison Altim can make honestly from what it has: the level now, less the level one
/// window length ago - which is the same point in the previous window, because the windows
/// are the same length.
/// </para>
/// <para>
/// It is a computation over local history, not a guess. Every way it can fail to have an
/// answer returns <see langword="null"/>, and the interface renders a null as an em dash:
/// no sample for that metric, a sample with no percentage, or a sample so old that the
/// window it measured had already rolled over before the instant being compared against.
/// That last one matters most - history is written only when a value changes, so the most
/// recent sample before an instant can be from any distance in the past, and a level from
/// two windows ago is not the previous window's level.
/// </para>
/// </remarks>
public static class UsagePacing
{
    /// <summary>
    /// The instant a comparison for this window is made against: one window length back.
    /// </summary>
    /// <param name="window">The window being paced.</param>
    /// <param name="now">The present instant.</param>
    /// <returns>The instant, or null when there is no window to step back by.</returns>
    public static DateTimeOffset? ComparisonInstant(LimitWindow? window, DateTimeOffset now) =>
        window is { Length: var length } && length > TimeSpan.Zero ? now - length : null;

    /// <summary>
    /// Compares a current level against what was standing one window ago.
    /// </summary>
    /// <param name="carryIn">
    /// The most recent sample of each metric before <paramref name="at"/>, which is exactly
    /// what <c>IUsageHistoryService.GetLatestBeforeAsync</c> returns.
    /// </param>
    /// <param name="metricKey">The metric being paced.</param>
    /// <param name="currentPercent">The level now.</param>
    /// <param name="at">The instant being compared against.</param>
    /// <returns>
    /// The difference in percentage points, positive for using more than last time, or
    /// <see langword="null"/> when there is not enough history to say. Never zero as a
    /// stand-in: zero means the two levels really were the same.
    /// </returns>
    public static double? Compare(
        IReadOnlyList<UsageSample> carryIn,
        string metricKey,
        double currentPercent,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(carryIn);
        ArgumentNullException.ThrowIfNull(metricKey);

        foreach (UsageSample sample in carryIn)
        {
            if (!string.Equals(sample.MetricKey, metricKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!UsageHistorySeries.HoldsAt(sample, at))
            {
                return null;
            }

            return UsagePercent.Normalise(sample.UsedPercent) is { } previous
                ? currentPercent - previous
                : null;
        }

        return null;
    }
}
