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
/// The concentric face as pixels: one sweep per provider, each on its own ring, each stopping
/// at its own angle, with a gap of page between them and nothing invented for a provider that
/// reported nothing.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is an assertion the object graph cannot answer. Two rings can be computed
/// perfectly and drawn on top of one another, in the wrong order, or at one another's angles,
/// and every structural test would still pass. The readings are deliberately far apart and
/// deliberately not in level order, so a renderer that sorted them, or that drew the same
/// reading twice, fails rather than looking plausible.
/// </para>
/// <para>
/// Both variants, every time. Dark is a designed palette rather than an inversion, so neither
/// one proves the other. Frames come through <see cref="PixelHost"/>, which copies the pixels
/// out and disposes the platform bitmap before returning.
/// </para>
/// </remarks>
public sealed class DialRingPixelTests
{
    /// <summary>The window the dial is laid out in, with room round it for the focus ring.</summary>
    private const double Room = 176d;

    /// <summary>The inset that leaves that room.</summary>
    private const double Inset = 16d;

    /// <summary>Both variants, so a face that only exists in Light fails here.</summary>
    public static TheoryData<string> Variants => ["Light", "Dark"];

    /// <summary>
    /// Each provider's arc is on its own ring, sweeping to its own level and stopping there.
    /// The outer ring carries the first reading whether or not it is the higher one.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void EachProvidersArcIsOnItsOwnRingAtItsOwnAngle(string name)
    {
        ThemeVariant variant = Variant(name);

        // The outer ring is the lower reading, so a renderer that put the higher one outside
        // fails here rather than looking tidy.
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", 33d, 80d),
                new DialReading(1, "Weekly (Codex)", 54d, 80d),
            ],
        };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        Color normal = Token(variant, "AltimDialNormalBrush");
        Color caution = Token(variant, "AltimDialCautionBrush");
        Color track = Token(variant, "AltimMeterTrackBrush");

        double outer = RingCentre(2, 0);
        double inner = RingCentre(2, 1);

        // The outer ring stops at 33 and has not reached half way, so it is the normal band
        // all the way and the track beyond.
        Assert.True(
            Ink.Near(frame.At(Polar(centre, outer, Dial.AngleFor(20d))), normal, 4),
            $"The outer ring is not swept at 20%: {frame.Describe(Dot(centre, outer, Dial.AngleFor(20d)))}");
        Assert.True(
            Ink.Near(frame.At(Polar(centre, outer, Dial.AngleFor(40d))), track, 4),
            $"The outer ring swept past 33%: {frame.Describe(Dot(centre, outer, Dial.AngleFor(40d)))}");

        // The inner ring stops at 54, so at 40 it is still sweeping and at 52 it is already
        // in the caution band. Its own angle, not the outer ring's.
        Assert.True(
            Ink.Near(frame.At(Polar(centre, inner, Dial.AngleFor(40d))), normal, 4),
            $"The inner ring is not swept at 40%: {frame.Describe(Dot(centre, inner, Dial.AngleFor(40d)))}");
        Assert.True(
            Ink.Near(frame.At(Polar(centre, inner, Dial.AngleFor(52d))), caution, 4),
            $"The inner ring is not in the caution band at 52%: "
                + frame.Describe(Dot(centre, inner, Dial.AngleFor(52d))));
        Assert.True(
            Ink.Near(frame.At(Polar(centre, inner, Dial.AngleFor(60d))), track, 4),
            $"The inner ring swept past 54%: {frame.Describe(Dot(centre, inner, Dial.AngleFor(60d)))}");
    }

    /// <summary>
    /// The rings do not touch. A radial cut across the face at a level both have swept to
    /// finds ink, then the page, then ink again: two bands with a clear gap, rather than one
    /// thick rail with a seam in it.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheRingsAreSeparatedByAGapOfPage(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", 33d, 80d),
                new DialReading(1, "Weekly (Codex)", 54d, 80d),
            ],
        };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        Color ground = Token(variant, "AltimSurfaceBrush");

        // Read at 20%, where both rings are swept, so the cut crosses two bands of ink.
        double degrees = Dial.AngleFor(20d);
        List<bool> painted = [];
        for (double radius = 68d; radius >= 50d; radius -= 1d)
        {
            painted.Add(!Ink.Near(frame.At(Polar(centre, radius, degrees)), ground, 6));
        }

        int runs = 0;
        for (int i = 0; i < painted.Count; i++)
        {
            if (painted[i] && (i == 0 || !painted[i - 1]))
            {
                runs++;
            }
        }

        Assert.Equal(2, runs);
    }

    /// <summary>
    /// A provider that reported nothing draws no sweep at all: its ring is an outline and the
    /// other provider's is a reading. A zero length arc at the start of the scale is the one
    /// picture that would read as a reported nothing.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AnUnreportedProviderDrawsNothingOnItsOwnRing(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", 62d, 80d),
                new DialReading(1, "Session (Codex)", null, 80d),
            ],
        };

        using PixelHost host = Show(dial, variant);
        Frame frame = host.Capture();

        Point centre = Centre(host, dial);
        Color normal = Token(variant, "AltimDialNormalBrush");
        Color caution = Token(variant, "AltimDialCautionBrush");
        Color track = Token(variant, "AltimMeterTrackBrush");
        Color ground = Token(variant, "AltimSurfaceBrush");

        double outer = RingCentre(2, 0);
        double inner = RingCentre(2, 1);

        // The reported ring is a reading, banded as usual.
        Assert.True(
            Ink.Near(frame.At(Polar(centre, outer, Dial.AngleFor(20d))), normal, 4),
            "The reported ring lost its sweep.");
        Assert.True(
            Ink.Near(frame.At(Polar(centre, outer, Dial.AngleFor(55d))), caution, 4),
            "The reported ring lost its caution band.");

        // The unreported ring is the page: no sweep, and not a filled track either, because a
        // filled track is what a reported nothing looks like.
        foreach (double level in (double[])[2d, 20d, 50d, 80d, 98d])
        {
            Color at = frame.At(Polar(centre, inner, Dial.AngleFor(level)));
            Assert.True(
                Ink.Near(at, ground, 6),
                $"The unreported ring is painted at {level}%: "
                    + frame.Describe(Dot(centre, inner, Dial.AngleFor(level))));
            Assert.False(Ink.Near(at, normal, 6), $"A sweep reached {level}% on nothing reported.");
            Assert.False(Ink.Near(at, track, 6), $"A track was filled at {level}% on nothing reported.");
        }

        // Its outline is still there, so the ring has not simply vanished: the provider is on
        // the face, saying it reported nothing. An outline is two hairlines with clear face
        // between them, so that is what is read: ink on the inner edge of this ring's band,
        // ink on its outer edge, and nothing in between.
        double thickness = Dial.RingThicknessFor(2);
        double middle = RingCentre(2, 1);
        double innerEdge = middle - (thickness / 2d);
        double outerEdge = middle + (thickness / 2d);
        IReadOnlyList<double> outline = BandInk(frame, centre, 2, 1, ground, 0d);

        Assert.True(
            outline.Any(radius => radius <= innerEdge + 1d),
            $"The unreported ring's inner edge at {innerEdge} was not outlined, so the "
                + $"provider is missing from the face. Painted at: {Radii(outline)}.");
        Assert.True(
            outline.Any(radius => radius >= outerEdge - 1d),
            $"The unreported ring's outer edge at {outerEdge} was not outlined, so the "
                + $"provider is missing from the face. Painted at: {Radii(outline)}.");
        Assert.DoesNotContain(
            outline,
            radius => radius > innerEdge + 1d && radius < outerEdge - 1d);
    }

    /// <summary>
    /// The ring being read is marked, and the mark follows the pointer from one ring to the
    /// other. A dial that named a ring only in words would leave the reader matching a
    /// sentence to one of two arcs by guesswork.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheRingBeingReadIsMarkedOnTheFace(string name)
    {
        ThemeVariant variant = Variant(name);
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", 33d, 80d),
                new DialReading(1, "Weekly (Codex)", 54d, 80d),
            ],
        };

        using PixelHost host = Show(dial, variant);

        Point centre = Centre(host, dial);

        // Read at 95%, past both readings and past the threshold index, so the only thing that
        // can change on either ring is the mark itself. Each box straddles the far edge of its
        // own ring - the outer ring's outer edge and the inner ring's inner edge - which puts
        // seven pixels of clear face between the two boxes.
        Rect outer = Edge(centre, 0);
        Rect inner = Edge(centre, 1);

        Frame before = host.Capture();

        Assert.True(dial.Focus(NavigationMethod.Tab), "The dial refused focus.");
        Frame onOuter = host.Capture();

        Assert.Equal(0, dial.FocusedRing);
        Assert.True(
            onOuter.DifferenceWith(before, outer) > 0,
            "The outer ring is not marked while it is being read.");
        Assert.Equal(0, onOuter.DifferenceWith(before, inner));

        dial.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Down,
            Source = dial,
        });

        Frame onInner = host.Capture();
        Assert.Equal(1, dial.FocusedRing);
        Assert.True(
            onInner.DifferenceWith(before, inner) > 0,
            "The inner ring is not marked while it is being read.");

        // And the outer ring goes back to being unmarked: the mark follows the reader rather
        // than accumulating one ring at a time.
        Assert.Equal(0, onInner.DifferenceWith(before, outer));
    }

    /// <summary>
    /// A small box straddling the far edge of one ring's band at 95%, where the mark is drawn
    /// and where no other ring reaches.
    /// </summary>
    private static Rect Edge(Point centre, int ring)
    {
        double thickness = Dial.RingThicknessFor(2);
        double radius = ring == 0
            ? RingCentre(2, 0) + (thickness / 2d)
            : RingCentre(2, 1) - (thickness / 2d);

        Point at = Polar(centre, radius, Dial.AngleFor(95d));
        return new Rect(Math.Round(at.X) - 1d, Math.Round(at.Y) - 1d, 3d, 3d);
    }

    /// <summary>The middle of one ring's band on a face carrying a given number.</summary>
    private static double RingCentre(int count, int ring)
    {
        double thickness = Dial.RingThicknessFor(count);
        double outer = (Dial.Size / 2d)
            - Dial.ScaleDepth
            - Dial.ScaleGap
            - (ring * (thickness + Dial.RingGap));

        return outer - (thickness / 2d);
    }

    /// <summary>
    /// The radii of a radial cut across one ring's own band that are not the page.
    /// </summary>
    /// <param name="frame">The captured face.</param>
    /// <param name="centre">The middle of the dial, in window coordinates.</param>
    /// <param name="count">How many readings the face carries.</param>
    /// <param name="ring">Which ring to cut across, outermost first.</param>
    /// <param name="ground">The page colour behind the face.</param>
    /// <param name="degrees">Where on the face to cut.</param>
    /// <returns>Every sampled radius that carries ink, inner edge first.</returns>
    /// <remarks>
    /// The band is the ring's middle plus or minus half its thickness. This scanned the
    /// middle plus or minus the WHOLE thickness - twice as wide as the thing it was
    /// measuring - which on a two ring face reached into the ring outside and counted the
    /// neighbouring provider's swept arc. The caller asked for two painted pixels and could
    /// reach two from that arc alone, so a ring drawn with no outline at all satisfied it.
    /// Half steps, because the hairline either side of the band is thinner than a sample
    /// stride and a whole step can walk straight over one.
    /// </remarks>
    private static IReadOnlyList<double> BandInk(
        Frame frame,
        Point centre,
        int count,
        int ring,
        Color ground,
        double degrees)
    {
        double thickness = Dial.RingThicknessFor(count);
        double middle = RingCentre(count, ring);
        List<double> painted = [];

        for (double radius = middle - (thickness / 2d);
            radius <= middle + (thickness / 2d);
            radius += 0.5d)
        {
            if (!Ink.Near(frame.At(Polar(centre, radius, degrees)), ground, 4))
            {
                painted.Add(radius);
            }
        }

        return painted;
    }

    /// <summary>A readable list of radii, for a failure message.</summary>
    private static string Radii(IReadOnlyList<double> radii) =>
        radii.Count == 0 ? "nothing" : string.Join(", ", radii);

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

    private static Point Polar(Point centre, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180d;
        return new Point(
            centre.X + (radius * Math.Sin(radians)),
            centre.Y - (radius * Math.Cos(radians)));
    }

    private static Rect Dot(Point centre, double radius, double degrees)
    {
        Point at = Polar(centre, radius, degrees);
        return new Rect(Math.Round(at.X) - 1d, Math.Round(at.Y) - 1d, 3d, 3d);
    }

    private static Color Token(ThemeVariant variant, string key)
    {
        Assert.True(
            DesignSystem.Ensure().TryGetResource(key, variant, out object? value),
            $"{key} does not resolve under {variant}.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
