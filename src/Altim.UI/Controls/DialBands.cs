using Altim.UI.Formatting;

namespace Altim.UI.Controls;

/// <summary>Which stretch of the scale a level has reached.</summary>
/// <remarks>
/// The aviation convention, in three: the range an instrument is expected to sit in, the
/// range that wants watching, and the range past the limit. Altim's dial colours the sweep
/// by which of these its last degree falls in, so the colour is a property of <b>where the
/// reading has got to</b> rather than a repaint of the whole instrument.
/// </remarks>
public enum DialBand
{
    /// <summary>Below <see cref="DialBands.CautionFrom"/>: nowhere near the ceiling.</summary>
    Normal,

    /// <summary>At or past half way, and short of the configured threshold.</summary>
    Caution,

    /// <summary>At or past the configured threshold.</summary>
    Exceeded,
}

/// <summary>
/// Where the dial's three bands begin, and what they say in words.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither boundary is a new number.</b> The caution band begins at
/// <see cref="InstrumentScale.HalfWay"/>, the landmark the scale already rules at and where
/// its graduation interval first halves. The exceeded band begins at the <i>configured
/// threshold</i>, which is the level Altim already notifies at and where the threshold index
/// already stands. Both colour changes therefore land on a mark that is drawn anyway: the
/// full depth graduation at 50 and the two hairline index at the threshold. That is what
/// keeps colour redundant here rather than load bearing - remove every hue and the two
/// boundaries are still marked, in the engraving's own ink, at the same two angles.
/// </para>
/// <para>
/// A threshold below half way collapses the caution band rather than reordering the bands:
/// the normal band runs to the threshold and the exceeded band starts there. A reading with
/// no configured threshold has no exceeded band at all, because there is nothing for it to
/// have exceeded, and inventing one would be inventing a limit.
/// </para>
/// </remarks>
public static class DialBands
{
    /// <summary>
    /// Where the caution band begins for a given threshold: half way, or the threshold
    /// itself when it stands below half way.
    /// </summary>
    /// <param name="threshold">The configured threshold, or null when there is none.</param>
    /// <returns>The level the caution band begins at.</returns>
    public static double CautionFrom(double? threshold) =>
        ExceededFrom(threshold) is { } limit
            ? Math.Min(InstrumentScale.HalfWay, limit)
            : InstrumentScale.HalfWay;

    /// <summary>Where the exceeded band begins, or null when no threshold is configured.</summary>
    /// <param name="threshold">The configured threshold, or null when there is none.</param>
    /// <returns>The level the exceeded band begins at.</returns>
    public static double? ExceededFrom(double? threshold) =>
        threshold is not { } limit || double.IsNaN(limit) ? null : Math.Clamp(limit, 0d, 100d);

    /// <summary>The band a level has reached.</summary>
    /// <param name="level">The level from 0 to 100, or null when unavailable.</param>
    /// <param name="threshold">The configured threshold, or null when there is none.</param>
    /// <returns>The band, or null when nothing is reported: an unreported reading is in no band.</returns>
    public static DialBand? BandFor(double? level, double? threshold)
    {
        if (level is not { } value || double.IsNaN(value))
        {
            return null;
        }

        if (ExceededFrom(threshold) is { } limit && value >= limit)
        {
            return DialBand.Exceeded;
        }

        return value >= CautionFrom(threshold) ? DialBand.Caution : DialBand.Normal;
    }

    /// <summary>
    /// What the bands mean, in words: which one the reading is in, and where the two
    /// boundaries stand.
    /// </summary>
    /// <param name="level">The level from 0 to 100, or null when unavailable.</param>
    /// <param name="threshold">The configured threshold, or null when there is none.</param>
    /// <returns>A sentence, or null when there is no reading to place in a band.</returns>
    /// <remarks>
    /// This is what a pointer and the keyboard both reveal. Colour is being asked to carry
    /// meaning here for the first time in this interface, and a colour code nobody can read
    /// out in words is a code the reader has to learn; these words are what stop that.
    /// </remarks>
    public static string? Words(double? level, double? threshold)
    {
        if (BandFor(level, threshold) is not { } band)
        {
            return null;
        }

        string caution = UsageFormat.Percent(CautionFrom(threshold)) ?? UsageFormat.Unknown;
        string? limit = UsageFormat.Percent(ExceededFrom(threshold));

        return (band, limit) switch
        {
            (DialBand.Exceeded, { } over) =>
                string.Concat("Past the ", over, " threshold. Caution begins at ", caution, "."),
            (DialBand.Caution, { } over) =>
                string.Concat(
                    "Caution band. It begins at ", caution, " and the threshold is at ", over, "."),
            (DialBand.Caution, null) => string.Concat("Caution band. It begins at ", caution, "."),
            (_, { } over) =>
                string.Concat(
                    "Normal band. Caution begins at ", caution, " and the threshold at ", over, "."),
            _ => string.Concat("Normal band. Caution begins at ", caution, "."),
        };
    }
}
