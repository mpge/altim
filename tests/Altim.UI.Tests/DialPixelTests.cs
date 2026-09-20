using Altim.UI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The dial as pixels: the engraving, the tightening, the threshold index, the sweep, and the
/// two states that may never look alike.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is one the object graph cannot answer. A scale can be computed
/// correctly and drawn nowhere, an index can be positioned correctly and painted in the colour
/// of the thing behind it, and an unreported reading can be drawn as a perfectly tidy zero.
/// The control's own arithmetic is asserted in <see cref="DialTests"/>; this suite only reads
/// the screen.
/// </para>
/// <para>
/// <b>The marks are radial, so they are read by sampling round a circle rather than along a
/// row.</b> A radial hairline cannot land on the pixel grid, so it comes out antialiased over
/// two columns and no single pixel is ever the engraving's own colour; the reader here
/// integrates coverage - how far each pixel is from the ground toward the ink - along the arc,
/// which both finds the mark and measures its weight. That is what lets this suite tell an
/// index from a graduation without asking about colour at all.
/// </para>
/// <para>
/// Both variants, every time. A scale that only exists in Light is a scale that does not
/// exist, and Dark is a designed palette rather than an inversion, so neither one proves the
/// other. Frames come through <see cref="PixelHost"/>, which copies the pixels out and
/// disposes the platform bitmap before returning: an undisposed frame takes the renderer down.
/// </para>
/// </remarks>
public sealed class DialPixelTests
{
    /// <summary>The window the dial is laid out in, with room round it for the focus ring.</summary>
    private const double Room = 176d;

    /// <summary>The inset that leaves that room.</summary>
    private const double Inset = 16d;

    /// <summary>A sample every quarter pixel along the arc: four to a hairline's width.</summary>
    private const double SampleStep = 0.25d;

    /// <summary>How much of the ink a pixel has to carry to count as part of a mark.</summary>
    private const double MarkCoverage = 0.25d;

    /// <summary>Both variants, so an engraving that only exists in Light fails here.</summary>
    public static TheoryData<string> Variants => ["Light", "Dark"];

    /// <summary>
    /// Every graduation the scale says it has reaches pixels, at the angle it belongs to, and
    /// there are exactly that many. A ring of ink would satisfy every other assertion here.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void EveryGraduationReachesPixels(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial { Value = 62d };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        Color engraving = Token(variant, "AltimMeterScaleBrush");
        Color ground = Token(variant, "AltimSurfaceBrush");

        IReadOnlyList<double> levels = Dial.GraduationsFor(Dial.Size / 2d);
        Assert.Equal(20, levels.Count);

        // One pixel inside the face's edge, where every graduation has ink.
        IReadOnlyList<Mark> marks = MarksRound(frame, centre, (Dial.Size / 2d) - 1d, ground, engraving);
        Assert.Equal(levels.Count, marks.Count);

        for (int i = 0; i < levels.Count; i++)
        {
            Assert.True(
                Math.Abs(marks[i].Angle - Dial.AngleFor(levels[i])) < 1d,
                $"The graduation for {levels[i]} is at {marks[i].Angle:F2} rather than "
                    + $"{Dial.AngleFor(levels[i]):F2}.");
        }
    }

    /// <summary>
    /// The three landmarks run the full depth of the engraving and the rest stop half way,
    /// which is the only thing telling a landmark from a graduation. Depth is read along each
    /// mark's own radius: two pixels is too fine a difference to find by sampling round a
    /// circle, where a pixel's own centre is already most of that far from where it was asked
    /// for.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void OnlyTheLandmarksRunTheFullDepth(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial { Value = 62d };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        Color engraving = Token(variant, "AltimMeterScaleBrush");
        Color ground = Token(variant, "AltimSurfaceBrush");

        List<double> landmarks = [];
        List<double> graduations = [];

        foreach (double level in Dial.GraduationsFor(Dial.Size / 2d))
        {
            double ink = InkOfMark(frame, centre, Dial.AngleFor(level), ground, engraving);
            (InstrumentScale.IsMajorGraduation(level) ? landmarks : graduations).Add(ink);
        }

        Assert.Equal(3, landmarks.Count);
        Assert.Equal(17, graduations.Count);

        string measured =
            $"landmarks {string.Join(", ", landmarks.Select(d => d.ToString("F2", null)))}; "
            + $"graduations {string.Join(", ", graduations.Select(d => d.ToString("F2", null)))}";

        // Every graduation reaches pixels at all, so none of the comparison below is being
        // won by a mark that was simply never drawn.
        Assert.True(graduations.Min() > 0.8d, $"A graduation barely reaches pixels: {measured}");

        // A landmark carries the ink of four pixels and a graduation of two, so the least
        // landmark carries half again as much as the most graduation. An engraving drawn all
        // one depth passes every other assertion in this file.
        Assert.True(
            landmarks.Min() >= 3d,
            $"A landmark does not run the engraving's full four pixels: {measured}");
        Assert.True(
            graduations.Max() <= 2.6d,
            $"A graduation runs past the half depth: {measured}");
    }

    /// <summary>
    /// The drawn engraving tightens toward the ceiling, and by at least half. The arithmetic
    /// is asserted elsewhere; this reads the marks off the screen, because a renderer that
    /// ignored the levels it was given and stepped evenly would satisfy the arithmetic and
    /// draw the wrong face.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheGraduationsTightenTowardTheCeiling(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial { Value = 62d };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        IReadOnlyList<Mark> marks = MarksRound(
            frame,
            Centre(host, dial),
            (Dial.Size / 2d) - 1d,
            Token(variant, "AltimSurfaceBrush"),
            Token(variant, "AltimMeterScaleBrush"));

        Assert.Equal(20, marks.Count);

        double[] gaps = new double[marks.Count - 1];
        for (int i = 1; i < marks.Count; i++)
        {
            gaps[i - 1] = marks[i].Angle - marks[i - 1].Angle;
            Assert.True(gaps[i - 1] > 0d, "The marks are not in order round the face.");
        }

        for (int i = 1; i < gaps.Length; i++)
        {
            Assert.True(
                gaps[i] <= gaps[i - 1] + 0.5d,
                $"The engraving widens from {gaps[i - 1]:F2} to {gaps[i]:F2} degrees.");
        }

        // At least halved, rather than merely not widened. A tape that stepped evenly but for
        // one mark reads as a tightened one until this is asserted.
        Assert.True(
            gaps[^1] * 2d <= gaps[0],
            $"The engraving does not tighten: finest {gaps[^1]:F2}, coarsest {gaps[0]:F2} degrees.");
    }

    /// <summary>
    /// The threshold index reads as an index without any help from colour: it is drawn in the
    /// engraving's own ink, it is the one mark that crosses the rail, and it carries twice the
    /// weight of a graduation.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheThresholdIndexIsLegibleWithoutColour(string name)
    {
        ThemeVariant variant = Variant(name);

        // 77 is deliberately not a graduation: an index sitting on one would prove nothing
        // about being able to tell the two marks apart.
        var dial = new Dial { Value = 40d, Threshold = 77d };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        Color engraving = Token(variant, "AltimMeterScaleBrush");
        Color index = Token(variant, "AltimMeterThresholdBrush");
        Color ground = Token(variant, "AltimSurfaceBrush");
        Color track = Token(variant, "AltimMeterTrackBrush");

        // One ink for the whole engraving. Nothing here is being carried by a hue.
        Assert.Equal(engraving, index);

        // Through the rail, where no graduation goes and nothing else is. Read over the
        // part of the rail the sweep has not reached, so the one mark found is a mark rather
        // than the edge of the reading.
        IReadOnlyList<Mark> crossing = MarksRound(
            frame,
            centre,
            RailCentre,
            track,
            index,
            over: Dial.AngleFor(50d),
            until: Dial.AngleFor(95d));

        Mark only = Assert.Single(crossing);
        AssertAt(Dial.AngleFor(77d), only);

        // Out in the engraving it stands beside the graduations, at twice their weight.
        IReadOnlyList<Mark> outside = MarksRound(frame, centre, (Dial.Size / 2d) - 1d, ground, engraving);
        Assert.Equal(21, outside.Count);

        Mark indexMark = Nearest(outside, Dial.AngleFor(77d));
        Mark graduation = Nearest(outside, Dial.AngleFor(50d));

        Assert.True(
            indexMark.Mass > graduation.Mass * 1.6d,
            $"The index carries {indexMark.Mass:F2} against a graduation's {graduation.Mass:F2}.");
        Assert.True(
            indexMark.Mass < graduation.Mass * 2.6d,
            $"The index carries {indexMark.Mass:F2}, which is more than twice a graduation's "
                + $"{graduation.Mass:F2}.");
    }

    /// <summary>
    /// The sweep stops at the reading. It starts at nothing used, runs clockwise, and the rail
    /// beyond it is the track: a sweep that ran the whole way round, or started somewhere
    /// else, would still pass every assertion about the engraving.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheSweepRunsFromNothingUsedToTheReading(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial { Value = 62d, Threshold = 80d };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        Color normal = Token(variant, "AltimDialNormalBrush");
        Color caution = Token(variant, "AltimDialCautionBrush");
        Color track = Token(variant, "AltimMeterTrackBrush");
        double reading = Dial.AngleFor(62d);

        Assert.True(
            Ink.Near(frame.At(Polar(centre, RailCentre, Dial.AngleFor(2d))), normal, 3),
            "The sweep does not start at nothing used.");
        Assert.True(
            Ink.Near(frame.At(Polar(centre, RailCentre, reading - 4d)), caution, 3),
            "The sweep stops short of the reading.");
        Assert.True(
            Ink.Near(frame.At(Polar(centre, RailCentre, reading + 4d)), track, 3),
            "The sweep carries on past the reading.");
        Assert.True(
            Ink.Near(frame.At(Polar(centre, RailCentre, Dial.AngleFor(99d))), track, 3),
            "The rail is filled at the ceiling on a reading of 62.");
    }

    /// <summary>
    /// The sweep changes colour where the bands change and nowhere else: normal up to half
    /// way, caution from there to the configured threshold, and past the threshold from there
    /// on. Both boundaries are levels the product already treats as meaningful, and each one
    /// stands on a mark the face draws anyway.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheSweepIsBandedAtTheTwoBoundaries(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial { Value = 95d, Threshold = 80d };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        Color normal = Token(variant, "AltimDialNormalBrush");
        Color caution = Token(variant, "AltimDialCautionBrush");
        Color exceeded = Token(variant, "AltimDialExceededBrush");

        // Three inks, not one repainted: the bands are told apart before anything is measured.
        Assert.NotEqual(normal, caution);
        Assert.NotEqual(caution, exceeded);
        Assert.NotEqual(normal, exceeded);

        foreach ((double level, Color want, string band) in (ValueTuple<double, Color, string>[])
        [
            (5d, normal, "normal"),
            (30d, normal, "normal"),
            (48d, normal, "normal"),
            (52d, caution, "caution"),
            (70d, caution, "caution"),
            (78d, caution, "caution"),
            (83d, exceeded, "exceeded"),
            (93d, exceeded, "exceeded"),
        ])
        {
            double degrees = Dial.AngleFor(level);
            Assert.True(
                Ink.Near(frame.At(Polar(centre, RailCentre, degrees)), want, 4),
                $"At {level}% the sweep is not the {band} band: "
                    + frame.Describe(Dot(centre, RailCentre, degrees)));
        }
    }

    /// <summary>
    /// With every colour taken away the reading is still there: the sweep's own length is a
    /// different grey from the track it stops in, and the two band boundaries are still marks
    /// on the engraving rather than colour changes.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    /// <remarks>
    /// This is the promise that lets colour be added at all. Colour says where the reading has
    /// got to and says nothing geometry does not also say, so a greyscale print, a photocopy
    /// or a reader who cannot separate two hues loses the emphasis and keeps the reading.
    /// </remarks>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheReadingSurvivesHavingEveryColourRemoved(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial { Value = 62d, Threshold = 80d };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        double track = Grey(frame.At(Polar(centre, RailCentre, Dial.AngleFor(90d))));

        // Where the sweep has reached, and where it has not, are different greys either side
        // of the reading, in all three bands: the length is legible with no hue at all.
        foreach (double level in (double[])[5d, 30d, 48d, 55d, 60d])
        {
            double swept = Grey(frame.At(Polar(centre, RailCentre, Dial.AngleFor(level))));
            Assert.True(
                Math.Abs(swept - track) > 0.15d,
                $"At {level}% the sweep and the track are the same grey: {swept:F3} against "
                    + $"{track:F3}.");
        }

        // The two boundaries are marks, not colour changes. Half way is a full depth
        // graduation and the threshold is the index, and both are found by the reader that
        // finds every other mark, which is told a ground and an ink and never a hue.
        IReadOnlyList<Mark> marks = MarksRound(
            frame,
            centre,
            (Dial.Size / 2d) - 1d,
            Token(variant, "AltimSurfaceBrush"),
            Token(variant, "AltimMeterScaleBrush"));

        Assert.Equal(20, marks.Count);
        AssertAt(Dial.AngleFor(50d), Nearest(marks, Dial.AngleFor(50d)));
        AssertAt(Dial.AngleFor(80d), Nearest(marks, Dial.AngleFor(80d)));
    }

    /// <summary>
    /// A full rail still shows where the threshold was. The sweep runs the whole arc at 100%,
    /// so an index drawn under it rather than over it would vanish exactly when the reading
    /// matters most.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AFullSweepStillShowsTheIndexAndTheEngraving(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial { Value = 100d, Threshold = 80d };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        Color caution = Token(variant, "AltimDialCautionBrush");
        Color exceeded = Token(variant, "AltimDialExceededBrush");
        Color index = Token(variant, "AltimMeterThresholdBrush");
        Color ground = Token(variant, "AltimSurfaceBrush");

        Assert.True(
            Ink.Near(frame.At(Polar(centre, RailCentre, Dial.AngleFor(99d))), exceeded, 3),
            "The rail is not filled at the ceiling on a reading of 100.");

        // The index stands exactly where the two bands meet, which is the one place a colour
        // change could be mistaken for it, so it is looked for from each side in turn against
        // that side's own band. A mark found in both is a mark rather than the seam.
        AssertAt(Dial.AngleFor(80d), Assert.Single(MarksRound(
            frame,
            centre,
            RailCentre,
            caution,
            index,
            over: Dial.AngleFor(60d),
            until: Dial.AngleFor(80d))));
        AssertAt(Dial.AngleFor(80d), Assert.Single(MarksRound(
            frame,
            centre,
            RailCentre,
            exceeded,
            index,
            over: Dial.AngleFor(80d),
            until: Dial.AngleFor(95d))));

        // And the two bands do meet there rather than somewhere near it.
        Assert.True(
            Ink.Near(frame.At(Polar(centre, RailCentre, Dial.AngleFor(76d))), caution, 4),
            "The caution band does not run up to the threshold: "
                + frame.Describe(Dot(centre, RailCentre, Dial.AngleFor(76d))));
        Assert.True(
            Ink.Near(frame.At(Polar(centre, RailCentre, Dial.AngleFor(84d))), exceeded, 4),
            "The exceeded band does not begin at the threshold: "
                + frame.Describe(Dot(centre, RailCentre, Dial.AngleFor(84d))));

        // The engraving is untouched by a full sweep: twenty graduations, and the index
        // standing on the one at 80 rather than in place of it.
        Assert.Equal(20, MarksRound(frame, centre, (Dial.Size / 2d) - 1d, ground, index).Count);
    }

    /// <summary>
    /// An unreported reading and a reading of nothing are not the same picture, and the
    /// difference is not a subtlety: one is a filled rail with an engraving and an index, the
    /// other is an outline. This is the rendering rule the product will not trade.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AnUnreportedReadingIsNotDrawnAsAZero(string name)
    {
        ThemeVariant variant = Variant(name);
        Color track = Token(variant, "AltimMeterTrackBrush");
        Color index = Token(variant, "AltimMeterThresholdBrush");
        Color ground = Token(variant, "AltimSurfaceBrush");

        var zeroDial = new Dial { Value = 0d, Threshold = 80d };
        var unknownDial = new Dial { Threshold = 80d };
        var fullDial = new Dial { Value = 100d, Threshold = 80d };

        using PixelHost zeroHost = Show(zeroDial, variant);
        Frame zero = zeroHost.Capture();
        Point centre = Centre(zeroHost, zeroDial);

        using PixelHost unknownHost = Show(unknownDial, variant);
        Frame unknown = unknownHost.Capture();

        using PixelHost fullHost = Show(fullDial, variant);
        Frame full = fullHost.Capture();

        // A reported nothing has a rail behind it. An unreported reading has the page.
        Assert.True(
            Ink.Near(zero.At(Polar(centre, RailCentre, 0d)), track, 3),
            $"A reading of nothing has no rail: {zero.Describe(Dot(centre, RailCentre, 0d))}");
        Assert.True(
            Ink.Near(unknown.At(Polar(centre, RailCentre, 0d)), ground, 3),
            $"An unreported reading filled its rail: {unknown.Describe(Dot(centre, RailCentre, 0d))}");

        // The engraving is the apparatus for reading a level, and there is no level to read.
        Assert.Equal(20, MarksRound(zero, centre, (Dial.Size / 2d) - 1d, ground, index).Count);
        Assert.Empty(MarksRound(unknown, centre, (Dial.Size / 2d) - 1d, ground, index));

        // The rail is still outlined, so an unreported reading is not simply nothing at all:
        // two hairlines across the band where a reported nothing has the whole eight filled.
        Assert.InRange(PaintedAcrossTheBand(unknown, centre, ground), 1, 4);
        Assert.True(
            PaintedAcrossTheBand(zero, centre, ground) >= 7,
            "A reading of nothing did not fill its rail across the band.");

        // And no sweep anywhere, in any band. A dial with nothing to report must not put its
        // reading at the bottom of the scale, which is the one thing that would read as a
        // reported zero.
        //
        // Every band is then looked for on a dial that paints all three, so none of the three
        // counts above is zero because the reader was hunting a colour this dial never wears.
        // The control is the band's own share of the rail rather than a floor, because a floor
        // cannot see a band bleeding over its neighbour: the sweep is painted whole and the two
        // upper bands are laid over it, so a band whose brush went missing is not a gap in the
        // rail but the band beneath it running on through.
        foreach (string band in (string[])
            ["AltimDialNormalBrush", "AltimDialCautionBrush", "AltimDialExceededBrush"])
        {
            Color ink = Token(variant, band);
            Assert.Equal(0, unknown.Count(Face(centre), c => Ink.Near(c, ink, 6)));

            (double from, double to) = BandSpan(band, fullDial.Threshold);
            double share = RailArea(from, to);
            int painted = full.Count(Face(centre), c => Ink.Near(c, ink, 6));

            Assert.True(
                painted >= share * 0.6d && painted <= share * 1.4d,
                $"{band} covers {painted} pixels on a dial that does report a reading, where "
                    + $"its own {from} to {to} share of the rail comes to {share:0}.");
        }
    }

    /// <summary>
    /// The focus ring reaches pixels, outside the face, and follows it round. The dial draws
    /// it itself rather than hosting a template part, so the failure this catches is the one
    /// every templated control had: a ring that exists and is clipped away.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheFocusRingPaintsRoundTheFace(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial { Value = 62d, Threshold = 80d };

        using PixelHost host = Show(dial, variant);
        Point centre = Centre(host, dial);
        Color ring = Token(variant, "AltimFocusRingBrush");
        Color ground = Token(variant, "AltimSurfaceBrush");

        // The ring is drawn astride a circle offset 2 from the face, so its ink lands between
        // 2 and 4 outside it. Three is the middle of that.
        double at = (Dial.Size / 2d) + 3d;
        double[] angles = [-150d, -90d, 0d, 90d, 150d, 180d];

        Frame before = host.Capture();
        foreach (double angle in angles)
        {
            Assert.True(
                RingAt(before, centre, at, angle, ground, ring) < 0.5d,
                $"A ring is drawn at {angle} degrees before the dial has focus.");
        }

        Assert.True(dial.Focus(NavigationMethod.Tab), "The dial refused focus.");
        Frame after = host.Capture();

        foreach (double angle in angles)
        {
            Assert.True(
                RingAt(after, centre, at, angle, ground, ring) >= 0.8d,
                $"The ring is missing at {angle} degrees: "
                    + after.Describe(Dot(centre, at, angle)));
        }
    }

    /// <summary>The radius the rail is centred on, which only the rail and the index reach.</summary>
    private static double RailCentre =>
        (Dial.Size / 2d) - Dial.ScaleDepth - Dial.ScaleGap - (Dial.RailThickness / 2d);

    /// <summary>The stretch of the scale one band's brush is the ink for.</summary>
    /// <param name="band">The token key of the band's brush.</param>
    /// <param name="threshold">The dial's configured threshold.</param>
    /// <returns>The levels the band runs from and to.</returns>
    /// <remarks>
    /// Read from <see cref="DialBands"/> rather than written out, so the two boundaries are
    /// the control's own and a test that agreed with a stale pair of numbers cannot exist.
    /// </remarks>
    private static (double From, double To) BandSpan(string band, double? threshold)
    {
        double caution = DialBands.CautionFrom(threshold);
        double exceeded = DialBands.ExceededFrom(threshold) ?? 100d;

        return band switch
        {
            "AltimDialNormalBrush" => (0d, caution),
            "AltimDialCautionBrush" => (caution, exceeded),
            _ => (exceeded, 100d),
        };
    }

    /// <summary>
    /// How much of the rail a stretch of the scale covers, in square device independent pixels:
    /// the arc it spans at the rail's own radius, times the rail's thickness.
    /// </summary>
    /// <param name="from">The level the stretch begins at.</param>
    /// <param name="to">The level it ends at.</param>
    /// <returns>The area.</returns>
    private static double RailArea(double from, double to) =>
        (to - from) / 100d * Dial.Sweep * Math.PI / 180d * RailCentre * Dial.RailThickness;

    private static PixelHost Show(Dial dial, ThemeVariant variant) =>
        PixelHost.Show(
            new Border { Padding = new Thickness(Inset), Child = new StackPanel { Children = { dial } } },
            variant,
            width: Room,
            height: Room);

    private static ThemeVariant Variant(string name) =>
        name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static Point Centre(PixelHost host, Dial dial)
    {
        Rect bounds = host.BoundsOf(dial);
        return new Point(bounds.X + (bounds.Width / 2d), bounds.Y + (bounds.Height / 2d));
    }

    /// <summary>A point on the face, from a radius and an angle clockwise from the top.</summary>
    private static Point Polar(Point centre, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180d;
        return new Point(
            centre.X + (radius * Math.Sin(radians)),
            centre.Y - (radius * Math.Cos(radians)));
    }

    /// <summary>One pixel at a polar position, for a failure message.</summary>
    private static Rect Dot(Point centre, double radius, double degrees)
    {
        Point at = Polar(centre, radius, degrees);
        return new Rect(Math.Round(at.X), Math.Round(at.Y), 1d, 1d);
    }

    /// <summary>The square the face occupies.</summary>
    private static Rect Face(Point centre) =>
        new(centre.X - (Dial.Size / 2d), centre.Y - (Dial.Size / 2d), Dial.Size, Dial.Size);

    /// <summary>
    /// How far a pixel has moved from the ground toward the ink, from 0 to 1. Only the
    /// channels the two colours actually differ in are counted, so a ground and an ink that
    /// share a channel do not dilute the answer with a zero.
    /// </summary>
    /// <remarks>
    /// A pixel that has overshot the ink, or moved the other way, is not a partly covered
    /// mark: it is a third colour that happens to lie on the same line. The sweep is darker
    /// than the engraving and the page is lighter than both, so without this the reader finds
    /// the fill and the page wherever it is looking for a mark - which it did, and which is
    /// how these tests learned to say so.
    /// </remarks>
    private static double CoverageOf(Color pixel, Color ground, Color ink)
    {
        double total = 0d;
        int counted = 0;

        foreach ((int from, int to, int got) in (ValueTuple<int, int, int>[])
        [
            (ground.R, ink.R, pixel.R),
            (ground.G, ink.G, pixel.G),
            (ground.B, ink.B, pixel.B),
        ])
        {
            if (Math.Abs(to - from) < 8)
            {
                continue;
            }

            double ratio = (got - from) / (double)(to - from);
            if (ratio is < -0.4d or > 1.4d)
            {
                return 0d;
            }

            total += Math.Clamp(ratio, 0d, 1d);
            counted++;
        }

        return counted == 0 ? 0d : total / counted;
    }

    /// <summary>
    /// The marks crossing a circle of a given radius: where each one is, and how much ink it
    /// carries. The mass is the coverage integrated along the arc, which for a mark crossing
    /// the circle square on is its pen's own width in pixels - so an index at two hairlines
    /// weighs twice what a graduation at one does, whatever the antialiasing did to either.
    /// </summary>
    private static IReadOnlyList<Mark> MarksRound(
        Frame frame,
        Point centre,
        double radius,
        Color ground,
        Color ink,
        double? over = null,
        double? until = null)
    {
        double from = over ?? (Dial.StartAngle - 2d);
        double to = until ?? (Dial.EndAngle + 2d);
        double arc = (to - from) * Math.PI / 180d * radius;
        int samples = Math.Max(1, (int)Math.Ceiling(arc / SampleStep));
        double step = arc / samples;

        List<Mark> marks = [];
        double mass = 0d;
        double weighted = 0d;

        for (int i = 0; i <= samples; i++)
        {
            double angle = from + ((to - from) * i / samples);
            Point at = Polar(centre, radius, angle);
            double coverage = CoverageOf(frame.At(at), ground, ink);

            if (coverage >= MarkCoverage)
            {
                mass += coverage;
                weighted += coverage * angle;
                continue;
            }

            if (mass > 0d)
            {
                marks.Add(new Mark(weighted / mass, mass * step));
                mass = 0d;
                weighted = 0d;
            }
        }

        if (mass > 0d)
        {
            marks.Add(new Mark(weighted / mass, mass * step));
        }

        return marks;
    }

    /// <summary>
    /// Asserts a mark stands where a level does. An angle read off a circle of pixels is good
    /// to about a pixel, which is half a degree out here; the marks it has to be told apart
    /// from are five degrees away.
    /// </summary>
    private static void AssertAt(double expected, Mark mark) =>
        Assert.True(
            Math.Abs(mark.Angle - expected) < 1.5d,
            $"The mark is at {mark.Angle:F2} degrees rather than {expected:F2}.");

    /// <summary>
    /// How much ring ink stands at an angle, taking the best of the pixels the ring's own
    /// weight can be spread over. A circle cannot land on the pixel grid either.
    /// </summary>
    private static double RingAt(
        Frame frame,
        Point centre,
        double radius,
        double angle,
        Color ground,
        Color ring)
    {
        double best = 0d;
        for (double offset = -1d; offset <= 1d; offset += 0.5d)
        {
            best = Math.Max(best, CoverageOf(frame.At(Polar(centre, radius + offset, angle)), ground, ring));
        }

        return best;
    }

    /// <summary>
    /// How much ink a mark carries, in pixels of full coverage. A mark twice as long carries
    /// twice as much, whatever the antialiasing did to either end.
    /// </summary>
    /// <remarks>
    /// Reading the furthest depth a mark reaches instead does not work here: a pixel's centre
    /// is already up to seven tenths of a pixel from the radius it was sampled at, and the
    /// difference being looked for is two pixels. Adding up the ink removes that, because the
    /// error is then on where each pixel sits rather than on how much ink there is. The count
    /// stops short of the rail, whose own antialiased edge is ink of a different kind.
    /// </remarks>
    private static double InkOfMark(Frame frame, Point centre, double angle, Color ground, Color ink)
    {
        double outer = Dial.Size / 2d;
        double from = outer - Dial.ScaleDepth - 0.75d;
        double to = outer + 0.75d;

        // Every distinct pixel in the wedge the mark occupies, counted once. Sampling along
        // the mark instead reads a different amount of ink at every angle, because a radial
        // hairline is spread over two columns and which of them a sample lands on is decided
        // by rounding. The wedge is four pixels wide, which holds the whole mark and reaches
        // none of its neighbours: the closest two marks on this face stand seven apart.
        Point near = Polar(centre, (from + to) / 2d, angle);
        double total = 0d;

        for (int y = (int)near.Y - 8; y <= (int)near.Y + 8; y++)
        {
            for (int x = (int)near.X - 8; x <= (int)near.X + 8; x++)
            {
                double dx = x - centre.X;
                double dy = centre.Y - y;
                double radius = Math.Sqrt((dx * dx) + (dy * dy));
                if (radius < from || radius > to)
                {
                    continue;
                }

                double bearing = Math.Atan2(dx, dy) * 180d / Math.PI;
                double away = Math.Abs(bearing - angle) * Math.PI / 180d * radius;
                if (away > 2d)
                {
                    continue;
                }

                total += CoverageOf(frame.At(x, y), ground, ink);
            }
        }

        return total;
    }

    /// <summary>
    /// How many pixels are painted along a radial cut across the band, at the top of the face.
    /// A filled rail paints the whole eight; an outlined one paints its two edges.
    /// </summary>
    private static int PaintedAcrossTheBand(Frame frame, Point centre, Color ground)
    {
        double outer = (Dial.Size / 2d) - Dial.ScaleDepth - Dial.ScaleGap;
        double inner = outer - Dial.RailThickness;

        int painted = 0;
        for (double radius = inner - 2d; radius <= outer + 2d; radius += 1d)
        {
            if (!Ink.Near(frame.At(Polar(centre, radius, 0d)), ground, 4))
            {
                painted++;
            }
        }

        return painted;
    }

    private static Mark Nearest(IReadOnlyList<Mark> marks, double angle)
    {
        Mark best = marks[0];
        foreach (Mark mark in marks)
        {
            if (Math.Abs(mark.Angle - angle) < Math.Abs(best.Angle - angle))
            {
                best = mark;
            }
        }

        return best;
    }

    /// <summary>
    /// What a colour comes out as with every hue taken away: its relative luminance, from 0
    /// to 1, through the sRGB transfer function rather than a channel average.
    /// </summary>
    private static double Grey(Color colour)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045d ? value / 12.92d : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }

        return (0.2126d * Linear(colour.R))
            + (0.7152d * Linear(colour.G))
            + (0.0722d * Linear(colour.B));
    }

    private static Color Token(ThemeVariant variant, string key)
    {
        Assert.True(
            DesignSystem.Ensure().TryGetResource(key, variant, out object? value),
            $"{key} does not resolve under {variant}.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    /// <summary>One mark read off the screen: where it is, and how much ink it carries.</summary>
    /// <param name="Angle">Its centre, in degrees clockwise from the top.</param>
    /// <param name="Mass">Its coverage integrated along the arc, in pixels of pen width.</param>
    private readonly record struct Mark(double Angle, double Mass);
}
