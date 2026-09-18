using Avalonia;
using Avalonia.Controls;

namespace Altim.UI.Controls;

/// <summary>
/// The one line weight in <c>docs/DESIGN.md</c>, resolved against the display that is
/// actually drawing it.
/// </summary>
/// <remarks>
/// <para>
/// A 1px rule is one <em>device independent</em> pixel. On a 125% or 150% display that is
/// 1.25 or 1.5 device pixels, which the rasteriser cannot fill: it spreads the line over
/// two rows at partial coverage and the hairline comes out grey, soft, and a different
/// grey on every rule depending on where it happened to land. The fix is to round the
/// weight to a whole number of device pixels and to place the line on a device pixel
/// boundary, both of which need the render scaling.
/// </para>
/// <para>
/// Rounding never goes below one device pixel: a rule that disappears is worse than a rule
/// that is fractionally heavy.
/// </para>
/// </remarks>
public static class Hairline
{
    /// <summary>The nominal weight from DESIGN.md, in device independent pixels.</summary>
    public const double Nominal = 1d;

    /// <summary>
    /// The render scaling a visual is being drawn at, or 1 when it is not in a tree yet.
    /// </summary>
    /// <param name="visual">The visual being rendered.</param>
    /// <returns>The scaling factor, never zero and never NaN.</returns>
    public static double ScaleOf(Visual? visual)
    {
        if (TopLevel.GetTopLevel(visual) is not { } root)
        {
            return 1d;
        }

        double scaling = root.RenderScaling;
        return double.IsNaN(scaling) || scaling <= 0d ? 1d : scaling;
    }

    /// <summary>
    /// The weight to draw a hairline at, in device independent pixels, so that it covers a
    /// whole number of device pixels.
    /// </summary>
    /// <param name="scale">The render scaling.</param>
    /// <returns>The snapped weight. 1 at 100%, 0.8 at 125%, 1.333… at 150%.</returns>
    public static double ThicknessFor(double scale)
    {
        if (double.IsNaN(scale) || scale <= 0d)
        {
            return Nominal;
        }

        double devicePixels = Math.Max(1d, Math.Round(Nominal * scale, MidpointRounding.AwayFromZero));
        return devicePixels / scale;
    }

    /// <summary>
    /// Moves an edge onto the nearest device pixel boundary, so a line starting there
    /// begins on a whole pixel rather than half way across one.
    /// </summary>
    /// <param name="position">The edge, in device independent pixels.</param>
    /// <param name="scale">The render scaling.</param>
    /// <returns>The snapped edge.</returns>
    public static double SnapEdge(double position, double scale)
    {
        if (double.IsNaN(position) || double.IsInfinity(position))
        {
            return position;
        }

        if (double.IsNaN(scale) || scale <= 0d)
        {
            return Math.Round(position, MidpointRounding.AwayFromZero);
        }

        return Math.Round(position * scale, MidpointRounding.AwayFromZero) / scale;
    }

    /// <summary>
    /// Rounds a length down to a whole number of device pixels.
    /// </summary>
    /// <param name="length">The length, in device independent pixels.</param>
    /// <param name="scale">The render scaling.</param>
    /// <returns>The longest whole pixel length that is no longer than the one asked for.</returns>
    /// <remarks>
    /// <see cref="SnapEdge"/> rounds to the nearest boundary, which is what a position wants
    /// and the opposite of what a size divided out of the room it has to fit in wants. A
    /// square rounded up is a fraction of a pixel per column, and across a year of columns
    /// that is a column more than the grid was given room for. Down, and it always fits.
    /// </remarks>
    public static double SnapDown(double length, double scale)
    {
        if (double.IsNaN(length) || double.IsInfinity(length))
        {
            return length;
        }

        if (double.IsNaN(scale) || scale <= 0d)
        {
            return Math.Floor(length);
        }

        return Math.Floor(length * scale) / scale;
    }

    /// <summary>
    /// Places a hairline of a given weight so that it lands on whole device pixels, from a
    /// position describing where the middle of the line wants to be.
    /// </summary>
    /// <param name="centre">The wanted centre, in device independent pixels.</param>
    /// <param name="thickness">The weight, from <see cref="ThicknessFor"/>.</param>
    /// <param name="scale">The render scaling.</param>
    /// <returns>The leading edge of the line.</returns>
    public static double SnapCentre(double centre, double thickness, double scale) =>
        SnapEdge(centre - (thickness / 2d), scale);
}
