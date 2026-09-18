using Altim.Core.Models;
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
/// The meter as pixels: the graduations, the tightening, the threshold index and the two
/// states that may never look alike.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is one the object graph cannot answer. A scale can be computed
/// correctly and drawn nowhere, an index can be positioned correctly and painted in the
/// colour of the thing behind it, and an unreported metric can be drawn as a perfectly tidy
/// zero. The control's own arithmetic is asserted in <see cref="MeterTests"/>; this suite
/// only reads the screen.
/// </para>
/// <para>
/// Both variants, every time. A scale that only exists in Light is a scale that does not
/// exist, and Dark is a designed palette rather than an inversion, so neither one proves the
/// other.
/// </para>
/// <para>
/// Frames come through <see cref="PixelHost"/>, which copies the pixels out and disposes the
/// platform bitmap before returning: an undisposed frame takes the renderer down.
/// </para>
/// </remarks>
public sealed class MeterPixelTests
{
    /// <summary>The rail width every test here lays a meter out at.</summary>
    /// <remarks>
    /// 240 puts every graduation on a whole pixel - the finest band is 2.5 per cent, which
    /// is 6 - so a mark that landed half a pixel out would be an antialiased pair rather
    /// than the one column these tests look for. It is also well under the 514 the narrowest
    /// meter in the running application measures, so the whole tape is carried.
    /// </remarks>
    private const double Width = 240d;

    /// <summary>Both variants, so a scale that only exists in Light fails here.</summary>
    public static TheoryData<string> Variants => ["Light", "Dark"];

    /// <summary>
    /// Every graduation the scale says it has reaches pixels, at the level it belongs to,
    /// and the three landmarks are the ones that run the full depth of the scale.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void EveryGraduationReachesPixels(string name)
    {
        ThemeVariant variant = Variant(name);
        var meter = new Meter { Value = 62d };

        using PixelHost host = Show(meter, variant);
        Frame frame = host.Capture();

        Rect bounds = host.BoundsOf(meter);
        Color engraving = Token(variant, "AltimMeterScaleBrush");
        int minor = MinorRow(bounds);
        int foot = FootRow(bounds);

        IReadOnlyList<double> levels = Meter.GraduationsFor(bounds.Width);
        Assert.Equal(Meter.Graduations.Count, levels.Count);

        foreach (double level in levels)
        {
            int x = Column(bounds, level);

            Assert.True(
                Ink.Near(frame.At(x, minor), engraving, 3),
                $"No graduation at {level}: {frame.Describe(new Rect(x, minor, 1d, 1d))}");

            // A major runs the whole depth of the scale and a minor stops half way. That is
            // the only thing telling them apart, so it is the thing worth reading back.
            Assert.True(
                Meter.IsMajorGraduation(level) == Ink.Near(frame.At(x, foot), engraving, 3),
                $"The graduation at {level} is the wrong depth: "
                    + frame.Describe(new Rect(x, foot, 1d, 1d)));
        }

        // Exactly that many marks and no more. A row of ink the whole way along would be a
        // rule rather than a scale, and would pass every assertion above it.
        Assert.Equal(
            levels.Count,
            frame.Count(new Rect(bounds.X, minor, bounds.Width, 1d), c => Ink.Near(c, engraving, 3)));
    }

    /// <summary>
    /// The drawn scale tightens toward the ceiling. The arithmetic is asserted elsewhere;
    /// this reads the marks off the screen, because a renderer that ignored the levels it
    /// was given and stepped evenly would satisfy the arithmetic and draw the wrong tape.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheGraduationsTightenTowardTheCeiling(string name)
    {
        ThemeVariant variant = Variant(name);
        var meter = new Meter { Value = 62d };

        using PixelHost host = Show(meter, variant);
        Frame frame = host.Capture();

        Rect bounds = host.BoundsOf(meter);
        Color engraving = Token(variant, "AltimMeterScaleBrush");
        int minor = MinorRow(bounds);

        List<int> marks = [];
        for (int x = (int)bounds.X; x < (int)bounds.Right; x++)
        {
            if (Ink.Near(frame.At(x, minor), engraving, 3))
            {
                marks.Add(x);
            }
        }

        Assert.Equal(Meter.Graduations.Count, marks.Count);

        for (int i = 2; i < marks.Count; i++)
        {
            Assert.True(
                marks[i] - marks[i - 1] <= marks[i - 1] - marks[i - 2],
                $"The scale widens at pixel {marks[i]}: {string.Join(", ", marks)}");
        }

        // At least halved, rather than merely not widened. The mark at the ceiling is held a
        // hairline inside the rail so that it is drawn at all, and that one pixel alone is
        // enough to make an evenly stepped tape look as though it tightened.
        Assert.True(
            (marks[^1] - marks[^2]) * 2 <= marks[1] - marks[0],
            $"The scale does not tighten: {string.Join(", ", marks)}");
    }

    /// <summary>
    /// The threshold index reads as an index without any help from colour: it is the one
    /// mark that crosses the rail, it runs the whole height of the control, and it is twice
    /// the weight of a graduation. It is also drawn in the scale's own ink, which is the
    /// point - nothing here is being carried by a hue.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheThresholdIndexIsLegibleWithoutColour(string name)
    {
        ThemeVariant variant = Variant(name);

        // 77 is deliberately not a graduation: an index sitting on one would prove nothing
        // about being able to tell the two marks apart.
        var meter = new Meter { Value = 40d, Threshold = 77d };

        using PixelHost host = Show(meter, variant);
        Frame frame = host.Capture();

        Rect bounds = host.BoundsOf(meter);
        Color index = Token(variant, "AltimMeterThresholdBrush");
        Color engraving = Token(variant, "AltimMeterScaleBrush");

        Assert.Equal(engraving, index);

        int x = (int)Math.Round(bounds.X + Meter.FillWidthFor(77d, bounds.Width));
        int rail = RailRow(bounds);
        int foot = FootRow(bounds);

        // Through the rail, where no graduation goes, and on through to the foot.
        Assert.Equal(2, Run(frame, rail, x, index));
        Assert.Equal(2, Run(frame, foot, x, index));
        Assert.Equal(2, frame.Count(new Rect(bounds.X, rail, bounds.Width, 1d), c => Ink.Near(c, index, 3)));

        // Twice the weight of the mark it has to be told apart from.
        Assert.Equal(1, Run(frame, foot, Column(bounds, 50d), engraving));
    }

    /// <summary>
    /// A full rail still shows where the threshold was. The fill runs the whole length at
    /// 100%, so an index drawn under it rather than over it would vanish exactly when the
    /// reading matters most.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AFullRailStillShowsTheIndexAndTheScale(string name)
    {
        ThemeVariant variant = Variant(name);
        var meter = new Meter { Value = 100d, Threshold = 80d };

        using PixelHost host = Show(meter, variant);
        Frame frame = host.Capture();

        Rect bounds = host.BoundsOf(meter);
        Color fill = Token(variant, "AltimMeterFillBrush");
        Color index = Token(variant, "AltimMeterThresholdBrush");
        int rail = RailRow(bounds);
        int minor = MinorRow(bounds);

        Assert.True(
            Ink.Near(frame.At((int)bounds.X + 10, rail), fill, 3),
            $"The rail is not filled at 100%: {frame.Describe(new Rect(bounds.X + 10d, rail, 1d, 1d))}");

        int x = (int)Math.Round(bounds.X + Meter.FillWidthFor(80d, bounds.Width));
        Assert.Equal(2, Run(frame, rail, x, index));

        // Every graduation, plus one column: the index stands on the graduation at 80 and
        // is a column wider than it.
        Assert.Equal(
            Meter.Graduations.Count + 1,
            frame.Count(new Rect(bounds.X, minor, bounds.Width, 1d), c => Ink.Near(c, index, 3)));
    }

    /// <summary>
    /// An unreported metric and a metric reported as nothing are not the same picture, and
    /// the difference is not a subtlety: one has a track, a scale and an index, and the
    /// other is an empty outline. This is the rendering rule the product will not trade.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AnUnreportedMetricIsNotDrawnAsAZero(string name)
    {
        ThemeVariant variant = Variant(name);
        Color track = Token(variant, "AltimMeterTrackBrush");
        Color ground = Token(variant, "AltimSurfaceBrush");
        Color index = Token(variant, "AltimMeterThresholdBrush");

        var zeroMeter = new Meter { Value = 0d, Threshold = 80d };
        var unknownMeter = new Meter { Value = null, Threshold = 80d };

        using PixelHost zeroHost = Show(zeroMeter, variant);
        Frame zero = zeroHost.Capture();
        Rect bounds = zeroHost.BoundsOf(zeroMeter);

        using PixelHost unknownHost = Show(unknownMeter, variant);
        Frame unknown = unknownHost.Capture();

        int rail = RailRow(bounds);
        int minor = MinorRow(bounds);
        var middle = new Rect(bounds.X + 40d, rail, 40d, 1d);
        var scale = new Rect(bounds.X, minor, bounds.Width, 1d);

        // A reported nothing has a track behind it. An unreported metric has the page.
        Assert.Equal(40, zero.Count(middle, c => Ink.Near(c, track, 2)));
        Assert.Equal(40, unknown.Count(middle, c => Ink.Near(c, ground, 2)));

        // The scale is the apparatus for reading a level, and there is no level to read.
        Assert.Equal(Meter.Graduations.Count + 1, zero.Count(scale, c => Ink.Near(c, index, 3)));
        Assert.Equal(0, unknown.Count(scale, c => Ink.Near(c, index, 3)));

        // The outline is still drawn, so an unreported metric is not simply nothing at all.
        var top = new Rect(bounds.X + 40d, bounds.Y, 40d, 1d);
        Assert.Equal(40, unknown.Count(top, c => Ink.Near(c, track, 3)));

        Assert.True(
            unknown.DifferenceWith(zero, new Rect(bounds.X, bounds.Y, bounds.Width, Meter.TotalHeight)) > 200,
            "An unreported metric and a zero are nearly the same picture.");
    }

    /// <summary>
    /// The focus ring reaches pixels, outside the control's own bounds. The meter draws it
    /// itself rather than hosting a template part, so the failure this catches is the same
    /// one every templated control had: a ring that exists and is clipped away.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheFocusRingPaintsOutsideTheMeter(string name)
    {
        ThemeVariant variant = Variant(name);
        var meter = new Meter { Value = 62d, Threshold = 80d };

        // Room around the control for a ring held 2px clear of it.
        var padded = new Border
        {
            Padding = new Thickness(20d),
            Child = new StackPanel { Children = { meter } },
        };

        using PixelHost host = PixelHost.Show(padded, variant, width: Width + 40d, height: 80d);

        Rect bounds = host.BoundsOf(meter);
        Color ring = Token(variant, "AltimFocusRingBrush");

        // The ring is drawn astride a rectangle offset 2 from the control, so its own ink
        // lands between 2 and 4 outside it. Three is the middle of that.
        var above = new Rect(bounds.X + 20d, bounds.Y - 3d, bounds.Width - 40d, 1d);

        Frame before = host.Capture();
        Assert.Equal(0, before.Count(above, c => Ink.Near(c, ring, 3)));

        Assert.True(meter.Focus(NavigationMethod.Tab), "The meter refused focus.");

        Frame after = host.Capture();
        int painted = after.Count(above, c => Ink.Near(c, ring, 3));

        Assert.True(
            painted >= (int)above.Width * 9 / 10,
            $"The ring painted {painted} of {(int)above.Width}: {after.Describe(above)}");
    }

    /// <summary>
    /// Reduced motion is visible in the frame, not only in a flag: the level changes and the
    /// very next frame is already drawn at the new level, where with motion allowed the same
    /// frame is still short of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves are deliberately symmetrical - same meter, same window, same number of
    /// captures - so the only thing that differs between them is what the operating system
    /// is reported to have said. A gate that never suppressed anything fails on the first
    /// assertion; a meter that had stopped animating altogether fails on the second.
    /// </para>
    /// <para>
    /// <b>The second assertion is the one assertion here that watches a clock</b>, because
    /// "the fill has not arrived yet" is only true while the 180ms is still running. The
    /// capture between them is a render tick and a bitmap copy of a 240x60 window, which is
    /// three orders of magnitude inside that. The first assertion - the one the change is
    /// actually about - cannot race anything, because with the animation suppressed there is
    /// no clock involved at all.
    /// </para>
    /// </remarks>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ReducedMotionDrawsTheNewLevelInTheVeryNextFrame(string name)
    {
        ThemeVariant variant = Variant(name);

        // What a meter that was simply at 80 all along looks like. Every assertion below is
        // against this picture rather than against a column count, so nothing here depends
        // on where a rounded end happens to antialias.
        var settled = new Meter { Value = 80d };
        using PixelHost settledHost = Show(settled, variant);
        Frame reference = settledHost.Capture();
        Rect area = settledHost.BoundsOf(settled);

        Frame reduced = FrameAfterChangingTo(80d, variant, MotionPreference.Reduced, area, reference);
        Frame travelling = FrameAfterChangingTo(80d, variant, MotionPreference.Full, area, reference);

        Assert.Equal(0, reduced.DifferenceWith(reference, area));
        Assert.True(
            travelling.DifferenceWith(reference, area) > 0,
            "One frame after the level changed the rail was already drawn at the new level"
                + " with motion allowed, so there was no animation to suppress and the"
                + " assertion above proves nothing.");
    }

    /// <summary>
    /// Lays a meter out at 20, changes it, and returns the very next frame.
    /// </summary>
    /// <param name="to">The level to change to.</param>
    /// <param name="variant">The theme variant to render under.</param>
    /// <param name="preference">What the platform is reported to say about motion.</param>
    /// <param name="area">The meter's bounds, which every host here shares.</param>
    /// <param name="reference">The picture of a meter already at the new level.</param>
    /// <returns>The frame captured immediately after the change.</returns>
    private static Frame FrameAfterChangingTo(
        double to, ThemeVariant variant, MotionPreference preference, Rect area, Frame reference)
    {
        using MotionScope scope = MotionScope.Of(preference);

        // No threshold: the index is drawn across the rail and would be one more thing the
        // comparison had to account for.
        var meter = new Meter { Value = 20d };
        using PixelHost host = Show(meter, variant);

        Assert.Equal(area, host.BoundsOf(meter));

        // It really does start somewhere else, or arriving at the new level would prove
        // nothing at all.
        Assert.True(
            host.Capture().DifferenceWith(reference, area) > 0,
            "A meter at 20 is already drawn the same as one at 80.");

        meter.Value = to;
        return host.Capture();
    }

    private static PixelHost Show(Meter meter, ThemeVariant variant) =>
        PixelHost.Show(
            new StackPanel { Children = { meter } },
            variant,
            width: Width,
            height: 60d);

    private static ThemeVariant Variant(string name) =>
        name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>The middle row of the rail, which only the fill, the track and the index reach.</summary>
    private static int RailRow(Rect bounds) => (int)(bounds.Y + (Meter.RailHeight / 2d));

    /// <summary>The first row of the scale, which every graduation reaches.</summary>
    private static int MinorRow(Rect bounds) => (int)(bounds.Y + Meter.RailHeight + Meter.ScaleGap);

    /// <summary>The last row of the scale, which only a major graduation and the index reach.</summary>
    private static int FootRow(Rect bounds) => (int)(bounds.Y + Meter.TotalHeight - 1d);

    /// <summary>The column a level is drawn at, clamped into the rail the way the renderer clamps it.</summary>
    private static int Column(Rect bounds, double level) =>
        (int)Math.Min(bounds.X + Meter.FillWidthFor(level, bounds.Width), bounds.Right - 1d);

    /// <summary>How many adjacent columns around a position are one colour.</summary>
    private static int Run(Frame frame, int y, int x, Color colour)
    {
        int width = 0;
        for (int at = x; at < frame.Width && Ink.Near(frame.At(at, y), colour, 3); at++)
        {
            width++;
        }

        for (int at = x - 1; at >= 0 && Ink.Near(frame.At(at, y), colour, 3); at--)
        {
            width++;
        }

        return width;
    }

    private static Color Token(ThemeVariant variant, string key)
    {
        Assert.True(
            DesignSystem.Ensure().TryGetResource(key, variant, out object? value),
            $"{key} does not resolve under {variant}.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
