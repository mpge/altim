using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// Normalisation for the one number every provider spells differently. A reading is
/// either a percentage Altim is willing to show or it is unavailable; it is never
/// coerced into zero.
/// </summary>
public static class UsagePercent
{
    /// <summary>A full window. Readings are clamped to this, never above it.</summary>
    public const double Full = 100d;

    /// <summary>
    /// The largest raw value accepted as a real reading. One point of slack above
    /// <see cref="Full"/> absorbs rounding at the provider; anything higher is the known
    /// defect where a Unix timestamp arrives in the percentage field.
    /// </summary>
    public const double MaxPlausible = UsageMetric.MaxPlausibleUsedPercent;

    /// <summary>
    /// Turns a raw provider reading into a value the UI may show.
    /// </summary>
    /// <param name="raw">
    /// The reading as the provider reported it. <see langword="null"/> means the provider
    /// did not report one.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the reading is unavailable, which covers a
    /// <see langword="null"/> input, a negative number, a non-finite number, and anything
    /// above <see cref="MaxPlausible"/>. A value between <see cref="Full"/> and
    /// <see cref="MaxPlausible"/> is clamped to <see cref="Full"/>. Every other value is
    /// returned unchanged. A <see langword="null"/> result is never an implied zero.
    /// </returns>
    public static double? Normalise(double? raw)
    {
        if (raw is not double value)
        {
            return null;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        if (value < 0d || value > MaxPlausible)
        {
            return null;
        }

        return value > Full ? Full : value;
    }

    /// <summary>
    /// True when <see cref="Normalise(double?)"/> would produce a value.
    /// </summary>
    /// <param name="raw">The reading as the provider reported it.</param>
    public static bool IsReported(double? raw) => Normalise(raw) is not null;

    /// <summary>
    /// Returns the metric with its <see cref="UsageMetric.UsedPercent"/> normalised.
    /// </summary>
    /// <param name="metric">The metric to normalise.</param>
    /// <returns>
    /// The same metric when the reading already passes, otherwise a copy whose percentage
    /// is clamped or set to <see langword="null"/>. Nothing else on the metric changes.
    /// </returns>
    public static UsageMetric Normalise(UsageMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        double? normalised = Normalise(metric.UsedPercent);
        return normalised is null && metric.UsedPercent is null
            ? metric
            : normalised == metric.UsedPercent ? metric : metric with { UsedPercent = normalised };
    }
}
