using Altim.Core.Models;

namespace Altim.Providers.Limits;

/// <summary>
/// Decides whether a reported percentage is a reading at all.
/// </summary>
/// <remarks>
/// A known provider defect writes an epoch timestamp into the percentage field before a
/// window has data. The value arrives as a perfectly well-formed number in the high
/// billions, and a meter that trusted it would render a full bar and fire a notification.
/// Anything above <see cref="UsageMetric.MaxPlausibleUsedPercent"/> is therefore discarded
/// at the parse boundary and the metric becomes unavailable — not clamped to 100, which
/// would turn a bug into a confident wrong answer.
/// </remarks>
public static class PercentReading
{
    /// <summary>
    /// Returns the percentage when it is plausible, and <see langword="null"/> otherwise.
    /// </summary>
    /// <param name="percent">The value the provider reported.</param>
    /// <returns>
    /// The value unchanged when it lies between 0 and
    /// <see cref="UsageMetric.MaxPlausibleUsedPercent"/> inclusive. <see langword="null"/>
    /// for a missing value, a negative one, one above that ceiling, and for a NaN or an
    /// infinity. Null means "not reported", which the UI renders as such and never as zero.
    /// </returns>
    public static double? Normalize(double? percent)
    {
        if (percent is not { } value || !double.IsFinite(value))
        {
            return null;
        }

        return value is >= 0d and <= UsageMetric.MaxPlausibleUsedPercent ? value : null;
    }
}
