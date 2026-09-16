using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// The one place a raw provider reading is turned into the reading the rest of the
/// application sees. Applied once, in the scheduler, before a reading is announced, so
/// that the aggregator, the threshold evaluator, the history writer and the views all
/// work from the same numbers rather than each re-deriving them.
/// </summary>
/// <remarks>
/// Two things happen here. Percentages are normalised, which clamps a provider rounding a
/// full window to 101 and drops the known defect where a Unix timestamp arrives in the
/// percentage field. And a metric whose window has already reset is reported as
/// unavailable: providers keep serving the last snapshot they have — the Claude status
/// line file is only rewritten when the tool next runs — so a passed reset instant means
/// the number beside it is a measurement of a window that is over. Showing it frozen
/// under "resets in under a minute" is worse than saying the value is not reported.
/// </remarks>
public static class UsageReadingNormaliser
{
    /// <summary>
    /// How far past its reset instant a window is allowed to be before its metric is
    /// treated as stale. This is clock-skew slack, not a grace period for the provider:
    /// the reset instant comes from another machine's idea of the time.
    /// </summary>
    public static readonly TimeSpan ResetGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Normalises every metric on a reading.
    /// </summary>
    /// <param name="usage">The reading as the provider produced it. Never <see langword="null"/>.</param>
    /// <param name="now">The current instant, from the scheduler's clock.</param>
    /// <returns>
    /// The same instance when nothing needed changing, so an unchanged reading costs no
    /// allocation, and a copy otherwise. Status, tokens and timestamps are untouched.
    /// </returns>
    public static ProviderUsage Normalise(ProviderUsage usage, DateTimeOffset now) =>
        Normalise(usage, now, ResetGrace);

    /// <summary>
    /// Normalises every metric on a reading with an explicit skew allowance.
    /// </summary>
    /// <param name="usage">The reading as the provider produced it. Never <see langword="null"/>.</param>
    /// <param name="now">The current instant, from the scheduler's clock.</param>
    /// <param name="grace">
    /// How far past its reset instant a window may be and still count as live.
    /// </param>
    /// <returns>The normalised reading, or the same instance when nothing changed.</returns>
    public static ProviderUsage Normalise(ProviderUsage usage, DateTimeOffset now, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(usage);

        List<UsageMetric>? rewritten = null;
        for (int i = 0; i < usage.Metrics.Count; i++)
        {
            UsageMetric metric = usage.Metrics[i];
            UsageMetric normalised = Normalise(metric, now, grace);
            if (rewritten is null)
            {
                if (ReferenceEquals(metric, normalised))
                {
                    continue;
                }

                rewritten = new List<UsageMetric>(usage.Metrics.Count);
                for (int seen = 0; seen < i; seen++)
                {
                    rewritten.Add(usage.Metrics[seen]);
                }
            }

            rewritten.Add(normalised);
        }

        return rewritten is null ? usage : usage with { Metrics = rewritten };
    }

    /// <summary>
    /// Normalises one metric.
    /// </summary>
    /// <param name="metric">The metric as the provider produced it. Never <see langword="null"/>.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="grace">
    /// How far past its reset instant the window may be and still count as live.
    /// </param>
    /// <returns>
    /// The same instance when nothing changed. Otherwise a copy with the percentage
    /// normalised, and, for a window whose reset instant has passed, no percentage and no
    /// reset instant at all: what Altim knows is the length of the window, not when the
    /// current one ends, and it never invents a reset time.
    /// </returns>
    public static UsageMetric Normalise(UsageMetric metric, DateTimeOffset now, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(metric);

        UsageMetric normalised = UsagePercent.Normalise(metric);
        if (metric.Window is not { ResetsAt: { } resetsAt } window || resetsAt + grace > now)
        {
            return normalised;
        }

        return normalised with { UsedPercent = null, Window = new LimitWindow(window.Length, null) };
    }

    /// <summary>
    /// Normalises one metric with the default skew allowance.
    /// </summary>
    /// <param name="metric">The metric as the provider produced it. Never <see langword="null"/>.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The normalised metric, or the same instance when nothing changed.</returns>
    public static UsageMetric Normalise(UsageMetric metric, DateTimeOffset now) =>
        Normalise(metric, now, ResetGrace);
}
