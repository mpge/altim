using Altim.UI.Controls;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The grid fills the width it is given, and every reading of where a square is agrees.
/// </summary>
/// <remarks>
/// <para>
/// The map used to draw a day at a constant 8px however much room it had, so a year sat at
/// 530px in an 860px panel with a third of the card empty. It now shares the width out between
/// the week columns. The square is therefore no longer a constant, and that is the whole risk
/// this suite exists for: the pitch is read by the render, by <see cref="UsageMap.HitTest"/>,
/// by <see cref="UsageMap.CellBounds"/> and by the ring drawn round the keyboard's square, and
/// if any one of them kept the old constant the map would <em>look</em> right while the
/// tooltip, the accessible name and the focus ring all pointed at the day next door.
/// </para>
/// <para>
/// A round trip is the assertion that cannot pass while two of those disagree: a point taken
/// from a square's own rectangle is handed to the hit test, and the day that comes back has to
/// be the day the rectangle was asked for. It is checked at widths the map was never written
/// against, because a constant and a fit agree at exactly one width and the default is it.
/// </para>
/// </remarks>
public sealed class UsageMapLayoutTests
{
    /// <summary>The first day of every row built here. A Wednesday, deliberately mid week.</summary>
    private static readonly DateOnly Start = new(2025, 9, 17);

    /// <summary>
    /// Widths a year fills without reaching the ceiling, so the fit is what decides the square.
    /// </summary>
    public static TheoryData<double> FillingWidths => [640d, 700d, 860d, 940d];

    /// <summary>
    /// The grid takes the largest square that fits the width it was given. Not merely a bigger
    /// one: one more device pixel per column would not fit, which is what "fills" means for a
    /// row of equal squares that cannot be split.
    /// </summary>
    /// <param name="width">The width to offer the map.</param>
    [AvaloniaTheory]
    [MemberData(nameof(FillingWidths))]
    public void TheGridTakesTheLargestSquareThatFitsTheWidth(double width)
    {
        var map = new UsageMap { Rows = [Year()] };
        using PixelHost host = Fit(map, width);

        double cell = Square(map, Start).Width;
        int columns = Columns(map);

        Assert.True(
            cell > map.MinCellSize,
            $"A year in {width} stayed at its smallest square of {cell}.");
        Assert.True(
            cell < map.MaxCellSize,
            $"A year in {width} reached the ceiling, so the fit decided nothing.");

        double spans = Spans(map, cell, columns);
        double oneMore = Spans(map, cell + 1d, columns);

        Assert.True(spans <= width, $"A grid of {columns} columns of {cell} needs {spans} of {width}.");
        Assert.True(oneMore > width, $"A square of {cell + 1d} would have fitted {width} as well.");
    }

    /// <summary>
    /// A square is square, and a block is seven of them tall, at a width the map was not
    /// written against.
    /// </summary>
    [AvaloniaFact]
    public void ASquareIsSquareAndABlockIsSevenOfThemTall()
    {
        var map = new UsageMap { Rows = [Year()] };
        using PixelHost host = Fit(map, 860d);

        Rect square = Square(map, Start);
        Assert.Equal(square.Width, square.Height, 6);

        // The Sunday after the first day, which is the top of its own column, and the Saturday
        // that closes it.
        DateOnly top = Start.AddDays(4);
        Rect first = Square(map, top);
        Rect last = Square(map, top.AddDays(6));

        Assert.Equal(first.X, last.X, 6);
        Assert.Equal((7d * first.Height) + (6d * map.CellGap), last.Bottom - first.Y, 6);
    }

    /// <summary>
    /// Every square is the same size and every one of them lands on whole device pixels.
    /// </summary>
    /// <param name="width">The width to offer the map.</param>
    /// <remarks>
    /// A square divided out of a width is a fraction far more often than not, and a fraction
    /// carried across fifty three columns puts each one a little further out than the last
    /// until some squares are a pixel wider than their neighbours and the grid reads as a
    /// wobble. Frames here are captured at 100%, so a whole device pixel is a whole device
    /// independent one and the two can be asserted together.
    /// </remarks>
    [AvaloniaTheory]
    [MemberData(nameof(FillingWidths))]
    public void EverySquareIsTheSameSizeAndOnWholeDevicePixels(double width)
    {
        var map = new UsageMap { Rows = [Year()] };
        using PixelHost host = Fit(map, width);

        Rect first = Square(map, Start);

        foreach (UsageMapCell cell in map.Rows![0].Cells)
        {
            Rect square = Square(map, cell.Day);

            Assert.Equal(first.Width, square.Width, 9);
            Assert.Equal(first.Height, square.Height, 9);
            Assert.Equal(Math.Round(square.X), square.X, 9);
            Assert.Equal(Math.Round(square.Y), square.Y, 9);
            Assert.Equal(Math.Round(square.Width), square.Width, 9);
            Assert.Equal(Math.Round(square.Height), square.Height, 9);
        }
    }

    /// <summary>
    /// The step from one square to the next is that square plus the gap, across and down. This
    /// is the pitch and the square agreeing: a rectangle drawn at one size on a grid laid out
    /// at another would leave the columns gappy or overlapping and fails here.
    /// </summary>
    [AvaloniaFact]
    public void TheStepBetweenSquaresIsTheSquareAndTheGap()
    {
        var map = new UsageMap { Rows = [Year()] };
        using PixelHost host = Fit(map, 860d);

        Rect square = Square(map, Start);
        Rect nextDay = Square(map, Start.AddDays(1));
        Rect nextWeek = Square(map, Start.AddDays(7));

        Assert.Equal(square.Height + map.CellGap, nextDay.Y - square.Y, 6);
        Assert.Equal(square.X, nextDay.X, 6);
        Assert.Equal(square.Width + map.CellGap, nextWeek.X - square.X, 6);
        Assert.Equal(square.Y, nextWeek.Y, 6);
    }

    /// <summary>
    /// Every square answers for its own day, at every width, from every corner of itself.
    /// </summary>
    /// <param name="width">The width to offer the map.</param>
    /// <remarks>
    /// The round trip runs both ways. A point taken out of a day's rectangle hits that day, and
    /// the rectangle the hit reports back is the one the point came from. A hit test still
    /// reading the old constant answers nothing at all at the middle of a grown square, because
    /// the middle is then past where it believes the square ends.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(240d)]
    [InlineData(420d)]
    [InlineData(700d)]
    [InlineData(860d)]
    [InlineData(1400d)]
    [InlineData(4000d)]
    public void EverySquareAnswersForItsOwnDayAtEveryWidth(double width)
    {
        var map = new UsageMap { Rows = [Year("Claude"), Year("Codex")] };
        using PixelHost host = Fit(map, width, height: 900d);

        foreach (int step in new[] { 0, 1, 6, 7, 8, 100, 201, 364 })
        {
            DateOnly day = Start.AddDays(step);

            for (int row = 0; row < 2; row++)
            {
                Rect square = Square(map, day, row);

                foreach (Point point in Corners(square))
                {
                    UsageMapHit? hit = map.HitTest(point);

                    Assert.True(hit is not null, $"{point} is inside {day:yyyy-MM-dd} and hit nothing.");
                    Assert.Equal(row, hit!.Value.RowIndex);
                    Assert.Equal(day, hit.Value.Cell.Day);
                    Assert.Equal(square, hit.Value.Bounds);
                    Assert.True(
                        map.CellBounds(hit.Value.RowIndex, hit.Value.Cell.Day)?.Contains(point) == true,
                        $"{point} hit {hit.Value.Cell.Day:yyyy-MM-dd}, whose square does not contain it.");
                }
            }
        }
    }

    /// <summary>
    /// The room between two squares belongs to no day. A hit test that took the whole pitch for
    /// a square would answer the day above for a point in the gap under it, and every tooltip
    /// near an edge would name the wrong date.
    /// </summary>
    /// <param name="width">The width to offer the map.</param>
    [AvaloniaTheory]
    [InlineData(420d)]
    [InlineData(860d)]
    [InlineData(4000d)]
    public void ThePointsBetweenTwoSquaresAreNoDay(double width)
    {
        var map = new UsageMap { Rows = [Year()] };
        using PixelHost host = Fit(map, width);

        Rect square = Square(map, Start);
        Rect below = Square(map, Start.AddDays(1));
        Rect across = Square(map, Start.AddDays(7));

        Assert.Null(map.HitTest(new Point(square.Center.X, (square.Bottom + below.Y) / 2d)));
        Assert.Null(map.HitTest(new Point((square.Right + across.X) / 2d, square.Center.Y)));
    }

    /// <summary>
    /// The square never falls below the floor or rises above the ceiling, and a map that had to
    /// stop at the floor draws past the width it was given rather than squeezing into it.
    /// </summary>
    /// <remarks>
    /// A column that shrank to fit a narrow window would take the squares with it until a day
    /// was a smudge, which is the thing the floor exists to refuse. What it costs is a grid
    /// wider than its panel, and that is the History page's scroll viewer's whole job.
    /// </remarks>
    [AvaloniaFact]
    public void TheSquareStopsAtTheFloorAndAtTheCeiling()
    {
        var narrow = new UsageMap { Rows = [Year()] };
        using (PixelHost host = Fit(narrow, 240d))
        {
            Assert.Equal(narrow.MinCellSize, Square(narrow, Start).Width, 6);

            double drawnTo = Rightmost(narrow) + narrow.FocusRingWidth;
            Assert.True(
                drawnTo > 240d,
                $"A year at its smallest square drew only {drawnTo} of 240, so it was squeezed.");
        }

        var wide = new UsageMap { Rows = [Year()] };
        using (PixelHost host = Fit(wide, 4000d, height: 900d))
        {
            Assert.Equal(wide.MaxCellSize, Square(wide, Start).Width, 6);
        }
    }

    /// <summary>
    /// A viewport too narrow for a year at its smallest square is handed a grid wider than
    /// itself, so the page has something to scroll.
    /// </summary>
    /// <remarks>
    /// This is the case the floor creates and the one the History page's scroll viewer is kept
    /// for. A map that reported the width it was told while drawing more than that would be
    /// painting outside its own rectangle, where a parent is free to clip it away and the last
    /// weeks of the year are simply unreachable.
    /// </remarks>
    [AvaloniaFact]
    public void ATooNarrowViewportIsHandedAGridWiderThanItself()
    {
        var map = new UsageMap { Rows = [Year()] };
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = map,
        };

        using PixelHost host = PixelHost.Show(scroller, ThemeVariant.Light, width: 400d, height: 400d);

        map.MinWidth = scroller.Viewport.Width;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(map.MinCellSize, Square(map, Start).Width, 6);
        Assert.True(
            map.DesiredSize.Width > scroller.Viewport.Width,
            $"A year at its smallest square asked for {map.DesiredSize.Width} of a "
                + $"{scroller.Viewport.Width} viewport, so it had been squeezed into it.");
        Assert.True(
            scroller.Extent.Width > scroller.Viewport.Width,
            "The panel has nothing to scroll, so the last weeks of the year cannot be reached.");
    }

    /// <summary>
    /// The floor and the ceiling are the theme's to set, and moving either one moves the grid.
    /// A map that cached a layout past a change of them would go on drawing the old square.
    /// </summary>
    [AvaloniaFact]
    public void MovingTheFloorOrTheCeilingMovesTheGrid()
    {
        var map = new UsageMap { Rows = [Year()] };
        using PixelHost host = Fit(map, 4000d, height: 900d);

        Assert.Equal(map.MaxCellSize, Square(map, Start).Width, 6);

        map.MaxCellSize = 24d;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(24d, Square(map, Start).Width, 6);

        map.MinCellSize = 32d;
        map.MaxCellSize = 32d;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(32d, Square(map, Start).Width, 6);
    }

    /// <summary>
    /// Offered no width at all - which is what a horizontally scrolling parent offers, because
    /// letting its content be wider than itself is the whole point of it - the map falls back
    /// rather than dividing a year of columns out of infinity. Told how wide the viewport is,
    /// it fills that instead.
    /// </summary>
    [AvaloniaFact]
    public void AMapOfferedNoWidthFallsBackAndOneToldTheViewportFillsIt()
    {
        var map = new UsageMap { Rows = [Year()] };
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = map,
        };

        using PixelHost host = PixelHost.Show(scroller, ThemeVariant.Light, width: 900d, height: 400d);

        Assert.Equal(map.MinCellSize, Square(map, Start).Width, 6);
        Assert.True(double.IsFinite(map.DesiredSize.Width), "A map offered infinity asked for infinity.");

        map.MinWidth = scroller.Viewport.Width;
        Dispatcher.UIThread.RunJobs();

        double cell = Square(map, Start).Width;
        Assert.True(cell > map.MinCellSize, $"A map told it is {map.MinWidth} wide drew a {cell} square.");
        Assert.True(
            Spans(map, cell + 1d, Columns(map)) > scroller.Viewport.Width,
            $"A square of {cell + 1d} would have fitted the {scroller.Viewport.Width} viewport as well.");
    }

    /// <summary>
    /// The ring still hugs the square once the grid has grown. It is drawn from the same
    /// rectangle everything else reads, so a ring on a stale pitch would be painted beside the
    /// square rather than round it.
    /// </summary>
    [AvaloniaFact]
    public void TheFocusRingStillHugsTheSquareOnceTheGridHasGrown()
    {
        var map = new UsageMap { Rows = [Year()] };
        using PixelHost host = Fit(map, 860d, height: 420d);

        Assert.True(Square(map, Start).Width > map.MinCellSize, "The grid did not grow.");
        Assert.True(map.Focus(NavigationMethod.Tab), "The map refused focus.");
        Dispatcher.UIThread.RunJobs();

        UsageMapHit focused = map.FocusedCell ?? throw new InvalidOperationException("No square is focused.");
        Rect origin = host.BoundsOf(map);
        Rect square = focused.Bounds.Translate(new Vector(origin.X, origin.Y));

        Color ring = Token(ThemeVariant.Light, "AltimFocusRingBrush");
        double weight = map.FocusRingWidth;
        Frame frame = host.Capture();

        foreach (Rect band in Bands(square, weight))
        {
            Assert.True(
                new Rect(0d, 0d, frame.Width, frame.Height).Contains(band),
                $"The ring's {band} band is outside the captured frame.");
            Assert.True(
                frame.Count(band, colour => Ink.Near(colour, ring, 3)) == (int)(band.Width * band.Height),
                $"The ring is missing a side at a fitted width: {frame.Describe(band)}");
        }
    }

    /// <summary>
    /// An assistive technology is told where a square moved to, without the year of children
    /// being thrown away and rebuilt.
    /// </summary>
    /// <remarks>
    /// The peers read their rectangle back from the map on every ask rather than remembering
    /// one, so a resize needs no invalidation: the same peer answers the new geometry. That is
    /// deliberate. Invalidating the children would tell a screen reader a thousand squares had
    /// changed every time the window was dragged, and it would re-read the lot.
    /// </remarks>
    [AvaloniaFact]
    public void TheAutomationPeersReportWhereTheSquaresMovedTo()
    {
        var map = new UsageMap { Rows = [Year()] };
        var box = new Decorator { Width = 420d, Child = map };
        using PixelHost host = PixelHost.Show(box, ThemeVariant.Light, width: 1500d, height: 700d);

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(map);
        IReadOnlyList<AutomationPeer> squares = peer.GetChildren();
        Assert.Equal(map.Rows![0].Cells.Count, squares.Count);

        AutomationPeer first = squares[0];
        AutomationPeer eighth = squares[7];
        Rect wasFirst = first.GetBoundingRectangle();
        Rect wasEighth = eighth.GetBoundingRectangle();

        Assert.Equal(Square(map, Start).Size, wasFirst.Size);
        Assert.Equal(Square(map, Start.AddDays(7)).X - Square(map, Start).X, wasEighth.X - wasFirst.X, 6);
        AssertReportedOnScreen(peer, map, first, Start);

        box.Width = 1400d;
        Dispatcher.UIThread.RunJobs();
        host.Capture();

        Rect nowFirst = first.GetBoundingRectangle();
        Assert.True(
            nowFirst.Width > wasFirst.Width,
            $"A square reported the same {nowFirst.Width} after the panel grew.");
        Assert.Equal(Square(map, Start).Size, nowFirst.Size);
        Assert.Equal(
            Square(map, Start.AddDays(7)).X - Square(map, Start).X,
            eighth.GetBoundingRectangle().X - nowFirst.X,
            6);
        AssertReportedOnScreen(peer, map, first, Start);

        // The same peers, not a rebuilt year of them.
        IReadOnlyList<AutomationPeer> again = peer.GetChildren();
        Assert.Same(squares[0], again[0]);
        Assert.Equal(squares.Count, again.Count);
    }

    /// <summary>A year of known days in one row.</summary>
    /// <param name="name">The row's label.</param>
    private static UsageMapRow Year(string name = "Claude")
    {
        var cells = new UsageMapCell[365];
        for (int index = 0; index < cells.Length; index++)
        {
            DateOnly day = Start.AddDays(index);
            long tokens = ((index % 40) + 1) * 1_000L;
            cells[index] = new UsageMapCell(day, tokens, null, IsKnown: true, $"{name} {day:yyyy-MM-dd}");
        }

        return new UsageMapRow(name, cells);
    }

    /// <summary>
    /// Hosts a map with exactly one width offered to it, whatever the window is.
    /// </summary>
    /// <param name="map">The map to host.</param>
    /// <param name="width">The width the map is measured against.</param>
    /// <param name="height">The window height.</param>
    private static PixelHost Fit(UsageMap map, double width, double height = 600d)
    {
        var box = new Decorator
        {
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = map,
        };

        return PixelHost.Show(box, ThemeVariant.Light, width: width + 40d, height: height);
    }

    /// <summary>One square's rectangle, in the map's own coordinates.</summary>
    /// <param name="map">The map to ask.</param>
    /// <param name="day">The day to find.</param>
    /// <param name="row">The row it is in.</param>
    /// <summary>
    /// Asserts a square's peer answers where the square is on the screen, not where it is on
    /// the map.
    /// </summary>
    /// <param name="mapPeer">The map's own peer, whose rectangle is the map on the screen.</param>
    /// <param name="map">The map.</param>
    /// <param name="squarePeer">The square's peer.</param>
    /// <param name="day">The day that square stands for.</param>
    /// <remarks>
    /// A size and the distance between two squares are both invariant under the translation
    /// from map coordinates to screen ones, so a peer that dropped it would satisfy every other
    /// assertion in this test while sending a screen reader to a point several hundred units
    /// from the square it had just named. The panel is narrower than the window and centred in
    /// it, which is what makes the offset large enough to be worth asserting.
    /// </remarks>
    private static void AssertReportedOnScreen(
        AutomationPeer mapPeer, UsageMap map, AutomationPeer squarePeer, DateOnly day)
    {
        Rect onScreen = mapPeer.GetBoundingRectangle();
        Rect square = squarePeer.GetBoundingRectangle();
        Rect local = Square(map, day);

        Assert.True(
            onScreen.X > 1d,
            $"The map stands at {onScreen.X}, where a translation that never happened looks "
                + "exactly like one that did.");
        Assert.Equal(local.X, square.X - onScreen.X, 6);
        Assert.Equal(local.Y, square.Y - onScreen.Y, 6);
    }

    private static Rect Square(UsageMap map, DateOnly day, int row = 0)
    {
        Rect? square = map.CellBounds(row, day);
        Assert.True(square is not null, $"The map has no square for {day:yyyy-MM-dd} in row {row}.");
        return square!.Value;
    }

    /// <summary>The right hand edge of the last square the map drew.</summary>
    /// <param name="map">The map to measure.</param>
    private static double Rightmost(UsageMap map)
    {
        double right = 0d;
        foreach (UsageMapCell cell in map.Rows![0].Cells)
        {
            if (map.CellBounds(0, cell.Day) is { } square)
            {
                right = Math.Max(right, square.Right);
            }
        }

        return right;
    }

    /// <summary>How many week columns the map drew.</summary>
    /// <param name="map">The map to count.</param>
    private static int Columns(UsageMap map)
    {
        HashSet<double> lefts = [];
        foreach (UsageMapCell cell in map.Rows![0].Cells)
        {
            if (map.CellBounds(0, cell.Day) is { } square)
            {
                lefts.Add(square.X);
            }
        }

        return lefts.Count;
    }

    /// <summary>The width a grid of one square size would need, insets and gaps included.</summary>
    /// <param name="map">The map whose gap and inset to use.</param>
    /// <param name="cell">The square size to price.</param>
    /// <param name="columns">How many week columns there are.</param>
    private static double Spans(UsageMap map, double cell, int columns) =>
        (map.FocusRingWidth * 2d) + (columns * (cell + map.CellGap)) - map.CellGap;

    /// <summary>Points inside a square that a constant sized hit test would miss.</summary>
    /// <param name="square">The square to probe.</param>
    private static Point[] Corners(Rect square) =>
    [
        square.Center,
        new(square.X + 0.5d, square.Y + 0.5d),
        new(square.Right - 0.5d, square.Bottom - 0.5d),
        new(square.X + 0.5d, square.Bottom - 0.5d),
        new(square.Right - 0.5d, square.Y + 0.5d),
    ];

    /// <summary>The four bands a ring of one width occupies round a square.</summary>
    /// <param name="square">The square the ring goes round.</param>
    /// <param name="weight">How thick the ring is.</param>
    private static Rect[] Bands(Rect square, double weight) =>
    [
        new(square.X, square.Y - weight, square.Width, weight),
        new(square.X, square.Bottom, square.Width, weight),
        new(square.X - weight, square.Y, weight, square.Height),
        new(square.Right, square.Y, weight, square.Height),
    ];

    private static Color Token(ThemeVariant variant, string key)
    {
        Assert.True(
            DesignSystem.Ensure().TryGetResource(key, variant, out object? value),
            $"{key} does not resolve under {variant}.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
