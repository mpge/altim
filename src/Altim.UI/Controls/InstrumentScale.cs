namespace Altim.UI.Controls;

/// <summary>
/// The one graduated scale in Altim's instrument panel: the levels it is marked at, which of
/// them are landmarks, how close two marks may stand, and how a level maps to a position
/// along it.
/// </summary>
/// <remarks>
/// <para>
/// Two controls read a level against this scale. <see cref="Meter"/> lays it out along a
/// straight rail; <see cref="Dial"/> bends the same cross section round a swept arc. They
/// share this type rather than each stating the scheme, because a card whose meters and a
/// panel whose dial were graduated differently would be two instruments, and the whole point
/// of a shared scale is that two readings can be compared by eye.
/// </para>
/// <para>
/// <b>Span, not width.</b> The arithmetic here is in one dimension: the distance from the
/// mark at nothing used to the mark at the ceiling, in device independent pixels. For the
/// meter that is the rail's width. For the dial it is the arc length the sweep covers at the
/// radius the graduations are drawn at, which is the radius where they stand closest
/// together. Either way a graduation is kept only when it stands
/// <see cref="MinimumGraduationPitch"/> clear of the one before it along that span.
/// </para>
/// <para>
/// <b>The mapping from a level to a position is always linear.</b> It is the graduations
/// that crowd toward the ceiling, never the mapping, because a scale that stretched would
/// misreport every level on it.
/// </para>
/// </remarks>
public static class InstrumentScale
{
    /// <summary>The thickness of the rail a level is drawn on.</summary>
    public const double RailThickness = 8d;

    /// <summary>The gap between the rail and the graduations beside it.</summary>
    public const double ScaleGap = 2d;

    /// <summary>The length of a major graduation.</summary>
    public const double ScaleDepth = 4d;

    /// <summary>The whole cross section: the rail, the gap and the graduations.</summary>
    public const double TotalDepth = RailThickness + ScaleGap + ScaleDepth;

    /// <summary>
    /// The closest two graduations are ever drawn, in device independent pixels. A hairline
    /// is one of those four, so three are clear between one graduation and the next; any
    /// tighter and the scale stops reading as marks and starts reading as a smear, which
    /// would report a solid block where the tape is finest.
    /// </summary>
    public const double MinimumGraduationPitch = 4d;

    /// <summary>The threshold index's weight, in hairlines.</summary>
    public const double IndexWeight = 2d;

    /// <summary>
    /// The levels the scale is graduated at, from 0 to 100, in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interval halves twice on the way up: every 10 to half way, every 5 from there to
    /// 80, every 2.5 over the last fifth. Altim exists to say how close a level is to a
    /// ceiling, so the tape is coarse where the answer is "nowhere near" and fine where the
    /// difference between two readings is the difference between carrying on and stopping.
    /// </para>
    /// <para>
    /// The two boundaries are the two levels the product already treats as meaningful: 50 is
    /// the half way rule the history tape draws, and 80 is the session threshold Altim ships
    /// with. Neither moves with the configured threshold. A scale that reshaped itself per
    /// metric would leave the readings on one surface measuring against different tapes.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<double> Graduations { get; } = BuildGraduations();

    /// <summary>
    /// Whether a level is one of the scale's major graduations, drawn the full
    /// <see cref="ScaleDepth"/> while the rest are drawn half of it.
    /// </summary>
    /// <param name="level">The level from 0 to 100.</param>
    /// <returns>True at nothing used, half way and the ceiling.</returns>
    /// <remarks>
    /// The same three levels the history tape rules at. Two readings on one page should be
    /// read against the same three landmarks whichever control is carrying them.
    /// </remarks>
    public static bool IsMajorGraduation(double level) => level is 0d or 50d or 100d;

    /// <summary>
    /// Maps a level to a distance along the scale. The mapping is linear and has no
    /// minimum: one percent of a 200px span is two pixels, not a token gesture toward
    /// visibility.
    /// </summary>
    /// <param name="level">The level from 0 to 100, or null when unavailable.</param>
    /// <param name="span">The distance from nothing used to the ceiling.</param>
    /// <returns>The distance, which is zero for a null level or a non-positive span.</returns>
    public static double PositionFor(double? level, double span)
    {
        if (span <= 0d || double.IsNaN(span) || level is not { } value || double.IsNaN(value))
        {
            return 0d;
        }

        return span * (Math.Clamp(value, 0d, 100d) / 100d);
    }

    /// <summary>
    /// The graduations a span of a given length can carry, in order.
    /// </summary>
    /// <param name="span">The distance from nothing used to the ceiling.</param>
    /// <returns>The levels to draw, which is empty for a span of nothing.</returns>
    /// <remarks>
    /// <para>
    /// A graduation is drawn only when it stands <see cref="MinimumGraduationPitch"/> clear
    /// of the one before it, and where a minor graduation and a major one compete the major
    /// wins - so the tape thins from the fine end as the span shortens and lands on the
    /// three landmarks rather than on an arbitrary subset. Nothing about the level a
    /// graduation stands at changes; the scale only ever loses marks it has no room to
    /// separate.
    /// </para>
    /// <para>
    /// This is the whole of the arithmetic. A renderer positions what it is given here and
    /// decides nothing, which is what lets a test assert the tightening without reading
    /// pixels off a bitmap.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<double> GraduationsFor(double span)
    {
        if (double.IsNaN(span) || span <= 0d)
        {
            return [];
        }

        List<double> kept = [];
        foreach (double level in Graduations)
        {
            double position = PositionFor(level, span);

            // A major graduation displaces the minor ones crowding it from behind, and
            // stands down only for another major: that keeps 0, 50 and 100 the last marks
            // left as a span shortens, rather than whichever minor happened to be first.
            if (IsMajorGraduation(level))
            {
                while (kept.Count > 0
                    && !IsMajorGraduation(kept[^1])
                    && position - PositionFor(kept[^1], span) < MinimumGraduationPitch)
                {
                    kept.RemoveAt(kept.Count - 1);
                }
            }

            if (kept.Count > 0 && position - PositionFor(kept[^1], span) < MinimumGraduationPitch)
            {
                continue;
            }

            kept.Add(level);
        }

        return kept;
    }

    private static double[] BuildGraduations()
    {
        List<double> levels = [];
        AddBand(levels, 0d, 50d, 10d);
        AddBand(levels, 50d, 80d, 5d);
        AddBand(levels, 80d, 100d, 2.5d);
        return [.. levels];
    }

    private static void AddBand(List<double> levels, double from, double to, double step)
    {
        // Stepped by index rather than by accumulation: an accumulated 2.5 would drift off
        // the band's own boundary and put a graduation at 99.999 instead of at the ceiling.
        for (int i = 0; ; i++)
        {
            double level = from + (i * step);
            if (level > to)
            {
                return;
            }

            if (levels.Count == 0 || levels[^1] < level)
            {
                levels.Add(level);
            }
        }
    }
}
