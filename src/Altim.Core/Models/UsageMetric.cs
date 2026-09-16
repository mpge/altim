namespace Altim.Core.Models;

/// <summary>
/// One reading a provider publishes about one limit.
/// </summary>
/// <param name="Key">
/// Stable identifier for the metric within its provider, for example
/// <c>five_hour</c>, <c>seven_day</c> or <c>codex:10080</c>. Used as the storage key,
/// so it must not change between releases for the same underlying limit.
/// </param>
/// <param name="Label">
/// Short human label such as "Session", "Weekly" or "5 hour". Display only.
/// </param>
/// <param name="UsedPercent">
/// Portion of the limit consumed, 0 to 100. <see langword="null"/> means the provider
/// did not report it, and the UI renders "Not reported by this provider". It is never
/// defaulted to zero. Values above <see cref="MaxPlausibleUsedPercent"/> are also
/// treated as unavailable, because a known provider defect returns a timestamp in
/// this field.
/// </param>
/// <param name="Window">
/// The period the limit is measured over. <see langword="null"/> means the provider
/// did not report a window, so no reset time can be shown.
/// </param>
/// <param name="Confidence">
/// Whether the provider documents this number or Altim inferred it.
/// </param>
public sealed record UsageMetric(
    string Key,
    string Label,
    double? UsedPercent,
    LimitWindow? Window,
    MetricConfidence Confidence)
{
    /// <summary>
    /// The largest <see cref="UsedPercent"/> Altim will accept as a real reading.
    /// One point of slack above 100 absorbs rounding at the provider.
    /// </summary>
    public const double MaxPlausibleUsedPercent = 101d;

    /// <summary>
    /// True when <see cref="UsedPercent"/> carries a reading Altim is willing to show.
    /// False for a null, for a negative number, and for anything above
    /// <see cref="MaxPlausibleUsedPercent"/>. When this is false the metric is
    /// unavailable and must be rendered as such, never as zero.
    /// </summary>
    public bool IsUsedPercentReported => UsedPercent is >= 0d and <= MaxPlausibleUsedPercent;
}
