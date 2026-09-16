using Altim.Core.Usage;

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
/// Portion of the limit consumed exactly as the provider reported it, which is not
/// the value to render: use <see cref="ReportedPercent"/> for that.
/// <see langword="null"/> means the provider did not report it, and the UI renders
/// "Not reported by this provider". It is never defaulted to zero. Values above
/// <see cref="MaxPlausibleUsedPercent"/> are treated as unavailable, because a known
/// provider defect returns a timestamp in this field.
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
    /// The value to render, and the only one: the raw reading normalised, which means
    /// clamped to a full window. <see langword="null"/> when the metric is unavailable,
    /// which is rendered as "Not reported by this provider" and never as zero.
    /// </summary>
    /// <remarks>
    /// This exists so that the predicate and the value cannot disagree. A raw reading of
    /// 101 is accepted — one point of slack for rounding at the provider — but 101% is
    /// not a thing to put on screen, so what comes back here is 100.
    /// </remarks>
    public double? ReportedPercent => UsagePercent.Normalise(UsedPercent);

    /// <summary>
    /// True when this metric carries a reading Altim is willing to show, which is exactly
    /// when <see cref="ReportedPercent"/> has a value. False for a null, for a negative
    /// number, for a non-finite number, and for anything above
    /// <see cref="MaxPlausibleUsedPercent"/>. When this is false the metric is
    /// unavailable and must be rendered as such, never as zero.
    /// </summary>
    public bool IsUsedPercentReported => ReportedPercent is not null;
}
