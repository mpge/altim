using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The two ends of the pairing, as pixels: pointing at a legend row marks that row's band on
/// the face, reading a band marks that band's row, and the keyboard does both.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which one is marked is the whole assertion.</b> A face that marked <em>a</em> band
/// whenever a row was pointed at would satisfy a test that only asked whether anything had
/// changed, and an off by one in ring order is exactly the defect that would produce. Every
/// assertion here names the band or the row it expects to change <em>and</em> the ones it
/// expects not to, so marking the neighbour fails rather than passes.
/// </para>
/// <para>
/// The probes are chosen so one ring's mark cannot be read as another's. A band's mark is a
/// hairline traced round it, so it lands on that band's own two radii, and the nearest radius
/// belonging to a different ring is two pixels away. A row's mark is read at the left edge of
/// its outline, level with the middle of the row: the rows stand four apart and their outlines
/// stand two clear of them, so the outlines meet in the gap but never at a row's own middle.
/// </para>
/// <para>
/// Both variants, every time. Dark is a designed palette rather than an inversion. Frames come
/// through <see cref="PixelHost"/>, which copies the pixels out and disposes the platform
/// bitmap before returning.
/// </para>
/// </remarks>
public sealed class DialLegendPixelTests
{
    /// <summary>How many rings the panel carries here.</summary>
    private const int Rings = 3;

    /// <summary>Both variants, so a pairing that only exists in Light fails here.</summary>
    public static TheoryData<string> Variants => ["Light", "Dark"];

    /// <summary>
    /// Pointing at a legend row traces the band that row names, and traces no other.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void PointingAtALegendRowMarksItsOwnBandAndNoOther(string name)
    {
        using PopupViewModel panel = Panel();
        using PixelHost host = Show(panel, name);

        Dial dial = Face(host);
        IReadOnlyList<DialLegendRow> rows = Rows(host);
        Assert.Equal(Rings, rows.Count);

        Frame rest = host.Capture();

        // The last row, deliberately not the middle one: on a face carrying three the middle
        // row is its own mirror, so an order reversed end to end would pass on it.
        Pointer(rows[2], InputElement.PointerEnteredEvent);
        Frame onLast = host.Capture();

        Assert.Equal(2, dial.MarkedRing);
        Assert.True(
            BandDifference(onLast, rest, host, dial, 2) > 0,
            "The row's own band is not traced.");
        Assert.Equal(0, BandDifference(onLast, rest, host, dial, 0));
        Assert.Equal(0, BandDifference(onLast, rest, host, dial, 1));

        // And the row itself is marked, so both ends of the pairing are lit at once.
        Assert.True(
            RowDifference(onLast, rest, host, rows[2]) > 0,
            "The row being pointed at is not marked.");
        Assert.Equal(0, RowDifference(onLast, rest, host, rows[0]));
        Assert.Equal(0, RowDifference(onLast, rest, host, rows[1]));
    }

    /// <summary>
    /// Reading a band on the face marks the row that names it, and marks no other row.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void PointingAtABandMarksItsOwnRowAndNoOther(string name)
    {
        using PopupViewModel panel = Panel();
        using PixelHost host = Show(panel, name);

        Dial dial = Face(host);
        IReadOnlyList<DialLegendRow> rows = Rows(host);

        Frame rest = host.Capture();

        // The innermost ring, which is the last row: a legend printed in the wrong order
        // fails here rather than looking tidy.
        PointAtBand(host, dial, 2);
        Frame onInner = host.Capture();

        Assert.Equal(2, dial.MarkedRing);
        Assert.True(
            RowDifference(onInner, rest, host, rows[2]) > 0,
            "The band's own row is not marked.");
        Assert.Equal(0, RowDifference(onInner, rest, host, rows[0]));
        Assert.Equal(0, RowDifference(onInner, rest, host, rows[1]));

        Assert.True(
            BandDifference(onInner, rest, host, dial, 2) > 0,
            "The band being read is not traced.");
        Assert.Equal(0, BandDifference(onInner, rest, host, dial, 0));
        Assert.Equal(0, BandDifference(onInner, rest, host, dial, 1));
    }

    /// <summary>
    /// The keyboard marks both ends exactly as a pointer does: arriving on the face marks the
    /// outermost band and its row, and stepping inward moves both marks together.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheKeyboardMarksTheSameTwoThingsAPointerDoes(string name)
    {
        using PopupViewModel panel = Panel();
        using PixelHost host = Show(panel, name);

        Dial dial = Face(host);
        IReadOnlyList<DialLegendRow> rows = Rows(host);

        Frame rest = host.Capture();

        Assert.True(dial.Focus(NavigationMethod.Tab), "The dial refused focus.");
        Frame onOuter = host.Capture();

        Assert.Equal(0, dial.MarkedRing);
        Assert.True(
            RowDifference(onOuter, rest, host, rows[0]) > 0,
            "Focusing the outermost band did not mark its row.");
        Assert.Equal(0, RowDifference(onOuter, rest, host, rows[1]));
        Assert.Equal(0, RowDifference(onOuter, rest, host, rows[2]));

        dial.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Down,
            Source = dial,
        });

        Frame onSecond = host.Capture();

        Assert.Equal(1, dial.MarkedRing);
        Assert.True(
            RowDifference(onSecond, rest, host, rows[1]) > 0,
            "Stepping inward did not mark the next row.");
        Assert.True(
            BandDifference(onSecond, rest, host, dial, 1) > 0,
            "Stepping inward did not trace the next band.");

        // The mark follows rather than accumulating: the row it left is unmarked again.
        Assert.Equal(0, RowDifference(onSecond, rest, host, rows[0]));
        Assert.Equal(0, BandDifference(onSecond, rest, host, dial, 0));
        Assert.Equal(0, RowDifference(onSecond, rest, host, rows[2]));
        Assert.Equal(0, BandDifference(onSecond, rest, host, dial, 2));
    }

    /// <summary>
    /// Arriving on a legend row with the keyboard traces that row's band, so the legend is not
    /// a hover only affordance.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ArrivingOnALegendRowWithTheKeyboardMarksItsBand(string name)
    {
        using PopupViewModel panel = Panel();
        using PixelHost host = Show(panel, name);

        Dial dial = Face(host);
        IReadOnlyList<DialLegendRow> rows = Rows(host);

        Frame rest = host.Capture();

        Assert.True(rows[2].Focus(NavigationMethod.Tab), "The legend row refused focus.");
        Frame onRow = host.Capture();

        Assert.Equal(2, dial.MarkedRing);
        Assert.True(
            BandDifference(onRow, rest, host, dial, 2) > 0,
            "Focusing a legend row did not trace its band.");
        Assert.Equal(0, BandDifference(onRow, rest, host, dial, 0));
        Assert.Equal(0, BandDifference(onRow, rest, host, dial, 1));
    }

    /// <summary>
    /// Nothing is marked while nothing is being read, and letting go puts the panel back
    /// exactly as it was rather than leaving a mark behind.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void NothingIsMarkedWhileNothingIsBeingRead(string name)
    {
        using PopupViewModel panel = Panel();
        using PixelHost host = Show(panel, name);

        Dial dial = Face(host);
        IReadOnlyList<DialLegendRow> rows = Rows(host);

        Assert.Null(dial.Marked);
        Assert.Null(dial.MarkedRing);
        Assert.All(rows, row => Assert.False(row.IsMarked));

        Frame rest = host.Capture();

        Pointer(rows[1], InputElement.PointerEnteredEvent);
        Frame marked = host.Capture();
        Assert.True(
            RowDifference(marked, rest, host, rows[1]) > 0,
            "Nothing was marked at all, so the comparison below proves nothing.");

        Pointer(rows[1], InputElement.PointerExitedEvent);
        Frame back = host.Capture();

        Assert.Null(dial.Marked);
        Assert.All(rows, row => Assert.False(row.IsMarked));

        for (int i = 0; i < Rings; i++)
        {
            Assert.Equal(0, BandDifference(back, rest, host, dial, i));
            Assert.Equal(0, RowDifference(back, rest, host, rows[i]));
        }
    }

    /// <summary>
    /// How many pixels on one band's own two radii differ between two frames.
    /// </summary>
    /// <remarks>
    /// The mark is a hairline traced round the whole band, so it lands on the band's inner and
    /// outer radii and on nothing between them. Sampled at several levels across the sweep, so
    /// a mark drawn over part of a band still registers, and never at a radius belonging to a
    /// ring beside it: on a face carrying three, two neighbouring radii stand two pixels apart.
    /// </remarks>
    private static int BandDifference(Frame after, Frame before, PixelHost host, Dial dial, int ring)
    {
        Rect face = host.BoundsOf(dial);
        var centre = new Point(face.X + (face.Width / 2d), face.Y + (face.Height / 2d));

        double thickness = Dial.RingThicknessFor(Rings);
        double outer = (Dial.Size / 2d)
            - Dial.ScaleDepth
            - Dial.ScaleGap
            - (ring * (thickness + Dial.RingGap));

        int different = 0;
        foreach (double level in (double[])[10d, 30d, 50d, 70d, 90d])
        {
            double degrees = Dial.AngleFor(level);
            foreach (double radius in (double[])[outer - thickness, outer])
            {
                Point at = Polar(centre, radius, degrees);
                if (after.At(at) != before.At(at))
                {
                    different++;
                }
            }
        }

        return different;
    }

    /// <summary>
    /// How many pixels on one row's own outline differ between two frames.
    /// </summary>
    /// <remarks>
    /// Read at the left edge of the outline, level with the middle of the row. The rows stand
    /// four apart and their outlines stand two clear of them, so two outlines can meet in the
    /// gap between two rows - but never at a row's own middle, which is what makes this say
    /// <em>which</em> row is marked rather than only that one is.
    /// </remarks>
    private static int RowDifference(Frame after, Frame before, PixelHost host, DialLegendRow row)
    {
        Rect bounds = host.BoundsOf(row);
        var edge = new Rect(bounds.X - 4d, bounds.Center.Y - 2d, 3d, 5d);

        return after.DifferenceWith(before, edge);
    }

    /// <summary>Puts the pointer on the middle of one of the dial's bands.</summary>
    /// <remarks>
    /// The position travels in the window's coordinates rather than the dial's: the argument
    /// is resolved against the root, so a point measured from the face's own corner arrives
    /// somewhere off the face entirely and the dial reports no ring at all.
    /// </remarks>
    private static void PointAtBand(PixelHost host, Dial dial, int ring)
    {
        double thickness = Dial.RingThicknessFor(Rings);
        double radius = (Dial.Size / 2d)
            - Dial.ScaleDepth
            - Dial.ScaleGap
            - (ring * (thickness + Dial.RingGap))
            - (thickness / 2d);

        Rect face = host.BoundsOf(dial);
        var centre = new Point(face.X + (face.Width / 2d), face.Y + (face.Height / 2d));
        Point at = Polar(centre, radius, Dial.AngleFor(20d));

        dial.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            dial,
            new Pointer(0, PointerType.Mouse, true),
            host.Window,
            at,
            0,
            new PointerPointProperties(),
            KeyModifiers.None));
    }

    private static void Pointer(DialLegendRow row, RoutedEvent<PointerEventArgs> which) =>
        row.RaiseEvent(new PointerEventArgs(
            which,
            row,
            new Pointer(0, PointerType.Mouse, true),
            row,
            default,
            0,
            new PointerPointProperties(),
            KeyModifiers.None));

    private static Point Polar(Point centre, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180d;
        return new Point(
            centre.X + (radius * Math.Sin(radians)),
            centre.Y - (radius * Math.Cos(radians)));
    }

    private static Dial Face(PixelHost host) =>
        Assert.Single(host.Window.GetVisualDescendants().OfType<Dial>());

    private static IReadOnlyList<DialLegendRow> Rows(PixelHost host) =>
        host.Window.GetVisualDescendants().OfType<DialLegendRow>().ToList();

    private static PixelHost Show(PopupViewModel panel, string variant) =>
        PixelHost.Show(
            new PopupView { DataContext = panel },
            variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
            width: 320d,
            height: 820d);

    /// <summary>Three providers, so the face carries the three rings the legend names.</summary>
    private static PopupViewModel Panel()
    {
        FakeUsageProvider[] providers =
        [
            new("claude", "Claude Code", Readings.Healthy("claude")),
            new("codex", "Codex", Readings.Healthy("codex", 41d)),
            new("gemini", "Gemini CLI", Readings.Healthy("gemini", 12d)),
        ];

        var panel = new PopupViewModel(providers, new TestClock(Readings.Now), AltimSettings.Default);
        for (int i = 0; i < providers.Length; i++)
        {
            panel.Providers[i].Apply(providers[i].Reading);
        }

        return panel;
    }
}
