using Altim.UI.Controls;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Reading the map without a pointer.
/// </summary>
/// <remarks>
/// <para>
/// The spec asks that hover and keyboard focus both show the tooltip and that the grid is
/// reachable by keyboard. The peers already gave an assistive technology a name per square; a
/// sighted keyboard user with no mouse still had no way to bring a day's words up, and no way
/// to see which day they were on.
/// </para>
/// <para>
/// Two rules here are load bearing rather than cosmetic. Focus lands on the first day the map
/// actually knows something about, because putting it on a leading unknown square would
/// announce a day Altim was not watching as though it were data. And the arrows stop at the
/// ends of the row they are in: a map whose keys ran off one provider's block into the next
/// would silently change whose figures are being read.
/// </para>
/// <para>
/// A map with nothing known in it is not focusable at all. That is not tidiness: a hosted
/// window can hand focus to the only focusable control in it, and an empty map that took focus
/// would paint a ring over the sentence it is supposed to be showing - and would break the
/// standing assertion in <see cref="UsageMapPixelTests"/> that a map given no rows draws
/// nothing whatsoever.
/// </para>
/// </remarks>
public sealed class UsageMapKeyboardTests
{
    /// <summary>The first day of every row built here. A Tuesday, deliberately not a week start.</summary>
    private static readonly DateOnly Start = new(2026, 9, 1);

    /// <summary>Both variants, so a ring that only exists in Light fails here.</summary>
    public static TheoryData<string> Variants => ["Light", "Dark"];

    /// <summary>
    /// Tab reaches the map, and the square it lands on is the first one the map knows
    /// something about rather than the first one it draws.
    /// </summary>
    [AvaloniaFact]
    public void TabMovesFocusIntoTheMapAndLandsOnTheFirstKnownDay()
    {
        var map = new UsageMap { Rows = [Row("Claude", days: 21, unknownHead: 3)] };
        var panel = new StackPanel();
        panel.Children.Add(map);

        using PixelHost host = PixelHost.Show(panel, ThemeVariant.Light, width: 420d, height: 220d);

        // Being on screen is not focus, and nothing has asked for a tooltip yet.
        Assert.Null(map.FocusedCell);
        Assert.Null(ToolTip.GetTip(map));

        Press(host, PhysicalKey.Tab);

        Assert.True(map.IsFocused, "Tab did not reach the map.");
        Assert.True(map.FocusedCell is not null, "The map took focus without focusing a square.");

        UsageMapHit focused = map.FocusedCell!.Value;
        Assert.Equal(0, focused.RowIndex);
        Assert.Equal(Start.AddDays(3), focused.Cell.Day);
        Assert.True(focused.Cell.IsKnown, "Focus landed on a day nothing is known about.");
    }

    /// <summary>
    /// Up and down move a day; left and right move a week. Weeks are columns and the days of a
    /// week read down a column, so that is what "by day" and "by week" mean on this grid.
    /// </summary>
    [AvaloniaFact]
    public void ArrowsMoveByDayAndByWeek()
    {
        var map = new UsageMap { Rows = [Row("Claude", days: 28)] };
        using PixelHost host = PixelHost.Show(map, ThemeVariant.Light, width: 420d, height: 220d);

        Enter(map);
        Assert.Equal(Start, Day(map));

        Press(host, PhysicalKey.ArrowDown);
        Assert.Equal(Start.AddDays(1), Day(map));

        Press(host, PhysicalKey.ArrowRight);
        Assert.Equal(Start.AddDays(8), Day(map));

        Press(host, PhysicalKey.ArrowLeft);
        Assert.Equal(Start.AddDays(1), Day(map));

        Press(host, PhysicalKey.ArrowUp);
        Assert.Equal(Start, Day(map));
    }

    /// <summary>
    /// The arrows stop at the ends of the row they are in. They never carry the reader into
    /// the next provider's block, which would change whose figures are being read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Standing still is not the same as doing nothing. An arrow that moves no square and is
    /// then left unhandled goes on up the tree, and on the History page the map sits inside a
    /// horizontally scrolling <c>ScrollViewer</c>: an arrow the map declined would scroll the
    /// whole year sideways under a reader who asked for the next day. So the edge presses are
    /// checked twice - the selection did not move, and the key did not escape the map.
    /// </para>
    /// <para>
    /// The escape check is the one the mutation pass had to add. An earlier version of this
    /// test hosted the map alone and only looked at the selection, and it stayed green against
    /// an implementation that marked nothing handled.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void ArrowsStopAtTheEdgesRatherThanWrappingIntoAnotherProvidersRow()
    {
        var map = new UsageMap { Rows = [Row("Claude", days: 10), Row("Codex", days: 10)] };
        var above = new Button { Content = "Above" };
        var below = new Button { Content = "Below" };
        var panel = new StackPanel();
        panel.Children.Add(above);
        panel.Children.Add(map);
        panel.Children.Add(below);

        // Unhandled keys only: a handler registered this way is not called for a key the map
        // consumed, so anything recorded here got past the map.
        List<Key> escaped = [];
        panel.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) => escaped.Add(e.Key),
            RoutingStrategies.Bubble);

        using PixelHost host = PixelHost.Show(panel, ThemeVariant.Light, width: 420d, height: 420d);

        Enter(map);
        Assert.Equal(Start, Day(map));

        // The handler is wired, and it does see a key the map has no business claiming. An
        // assertion that nothing escaped is worth nothing if nothing could have.
        Press(host, PhysicalKey.A);
        Assert.Equal([Key.A], escaped);
        escaped.Clear();

        // The oldest day of the first row: neither a day back nor a week back exists.
        Press(host, PhysicalKey.ArrowUp);
        Assert.True(map.IsFocused, "An arrow at the top of the row carried focus out of the map.");
        Assert.Equal(Start, Day(map));
        Assert.Equal(0, RowIndex(map));

        Press(host, PhysicalKey.ArrowLeft);
        Assert.True(map.IsFocused, "An arrow at the left of the row carried focus out of the map.");
        Assert.Equal(Start, Day(map));
        Assert.Equal(0, RowIndex(map));

        for (int step = 0; step < 9; step++)
        {
            Press(host, PhysicalKey.ArrowDown);
        }

        Assert.Equal(Start.AddDays(9), Day(map));
        Assert.Equal(0, RowIndex(map));

        // The newest day of the first row. Codex's block sits directly below it on screen and
        // the arrows must not fall into it.
        Press(host, PhysicalKey.ArrowDown);
        Assert.True(map.IsFocused, "An arrow at the end of the row carried focus out of the map.");
        Assert.Equal(Start.AddDays(9), Day(map));
        Assert.Equal(0, RowIndex(map));

        Press(host, PhysicalKey.ArrowRight);
        Assert.True(map.IsFocused, "An arrow at the right of the row carried focus out of the map.");
        Assert.Equal(Start.AddDays(9), Day(map));
        Assert.Equal(0, RowIndex(map));

        Assert.False(above.IsFocused, "An unconsumed arrow reached the control above the map.");
        Assert.False(below.IsFocused, "An unconsumed arrow reached the control below the map.");
        Assert.True(
            escaped.Count == 0,
            $"The map let {string.Join(", ", escaped)} past it for an ancestor to act on.");
    }

    /// <summary>
    /// Home and End go to the ends of what the row knows, not to the ends of the grid it was
    /// drawn on. A row whose newest days are unknown answers End with its last real day rather
    /// than with a square that has nothing to say.
    /// </summary>
    [AvaloniaFact]
    public void HomeAndEndGoToTheFirstAndLastKnownDayOfTheRow()
    {
        var map = new UsageMap { Rows = [Row("Claude", days: 20, unknownHead: 3, unknownTail: 2)] };
        using PixelHost host = PixelHost.Show(map, ThemeVariant.Light, width: 420d, height: 220d);

        Enter(map);

        Press(host, PhysicalKey.End);
        Assert.Equal(Start.AddDays(17), Day(map));
        Assert.True(Cell(map).IsKnown, "End landed on a day nothing is known about.");

        Press(host, PhysicalKey.Home);
        Assert.Equal(Start.AddDays(3), Day(map));
        Assert.True(Cell(map).IsKnown, "Home landed on a day nothing is known about.");
    }

    /// <summary>
    /// A ring is painted round the focused square, in the design system's focus colour, in
    /// both variants. Without it a keyboard reader hears a day and cannot see which one.
    /// </summary>
    /// <param name="variant">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheFocusRingIsPaintedRoundTheFocusedSquare(string variant)
    {
        ThemeVariant theme = Variant(variant);
        var map = new UsageMap { Rows = [Row("Claude", days: 28)] };

        using PixelHost host = PixelHost.Show(map, theme, width: 420d, height: 220d);

        Color ground = Token(theme, "AltimSurfaceBrush");
        Color ring = Token(theme, "AltimFocusRingBrush");
        double width = map.FocusRingWidth;
        Assert.True(width > 0d, "The map was given no focus ring width.");

        Rect square = Square(host, map, row: 0, Start);
        Rect[] bands = Bands(square, width);

        Frame before = host.Capture();
        foreach (Rect band in bands)
        {
            Assert.True(
                before.Count(band, colour => !Ink.Near(colour, ground, 2)) == 0,
                $"An unfocused map already painted a ring: {before.Describe(band)}");
        }

        Enter(map);
        Assert.Equal(Start, Day(map));

        Frame after = host.Capture();
        foreach (Rect band in bands)
        {
            Assert.True(
                after.Count(band, colour => Ink.Near(colour, ring, 3)) == (int)(band.Width * band.Height),
                $"The focus ring is missing a side ({ring}): {after.Describe(band)}");
        }

        // The square still reads as its own value: the ring goes round it, not over it.
        Rect middle = Inside(square);
        Assert.True(
            after.Count(middle, colour => Ink.Near(colour, ring, 3)) < (int)(middle.Width * middle.Height),
            $"The ring was painted over the square rather than round it: {after.Describe(middle)}");
    }

    /// <summary>
    /// Focus brings up the words hover brings up. Not the same shape of words: the very same
    /// string, so the two can never drift into two descriptions of one day.
    /// </summary>
    [AvaloniaFact]
    public void FocusShowsTheSameWordsHoverShows()
    {
        var map = new UsageMap { Rows = [Row("Claude", days: 28)] };
        var away = new Button { Content = "Away" };
        var panel = new StackPanel();
        panel.Children.Add(map);
        panel.Children.Add(away);

        using PixelHost host = PixelHost.Show(panel, ThemeVariant.Light, width: 420d, height: 260d);

        Point origin = host.BoundsOf(map).TopLeft;
        UsageMapCell hovered = map.Rows![0].Cells[5];

        host.Window.MouseMove(Centre(map, origin, row: 0, hovered.Day), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(hovered.Detail, ToolTip.GetTip(map));

        host.Window.MouseMove(new Point(origin.X + 2000d, origin.Y + 2000d), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Enter(map);
        for (int step = 0; step < 5; step++)
        {
            Press(host, PhysicalKey.ArrowDown);
        }

        Assert.Equal(hovered.Day, Day(map));
        Assert.Same(hovered.Detail, ToolTip.GetTip(map));
        Assert.True(ToolTip.GetIsOpen(map), "The focused square's words were set but never shown.");

        // Leaving takes the words with it. A tip left standing would caption whatever the
        // reader moved on to.
        Assert.True(away.Focus(NavigationMethod.Tab), "The button refused focus.");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(map.FocusedCell);
        Assert.Null(ToolTip.GetTip(map));
    }

    /// <summary>
    /// Ctrl and an arrow steps between providers. Without it the arrows' refusal to leave a
    /// row would make every provider but the first unreachable from the keyboard, which is the
    /// opposite of what the spec asks for.
    /// </summary>
    [AvaloniaFact]
    public void ControlAndAnArrowStepsBetweenProvidersAndStopsAtTheLastRow()
    {
        var map = new UsageMap
        {
            Rows =
            [
                Row("Claude", days: 14),
                Row("Codex", days: 14),
                Row("Combined", days: 14, combined: true),
            ],
        };

        using PixelHost host = PixelHost.Show(map, ThemeVariant.Light, width: 420d, height: 440d);

        Enter(map);
        Press(host, PhysicalKey.ArrowDown);
        Assert.Equal(Start.AddDays(1), Day(map));
        Assert.Equal(0, RowIndex(map));

        Press(host, PhysicalKey.ArrowDown, RawInputModifiers.Control);
        Assert.Equal(1, RowIndex(map));
        Assert.Equal(Start.AddDays(1), Day(map));

        Press(host, PhysicalKey.ArrowDown, RawInputModifiers.Control);
        Assert.Equal(2, RowIndex(map));
        Assert.Equal(Start.AddDays(1), Day(map));

        Press(host, PhysicalKey.ArrowDown, RawInputModifiers.Control);
        Assert.Equal(2, RowIndex(map));

        Press(host, PhysicalKey.ArrowUp, RawInputModifiers.Control);
        Assert.Equal(1, RowIndex(map));

        Press(host, PhysicalKey.ArrowUp, RawInputModifiers.Control);
        Assert.Equal(0, RowIndex(map));

        Press(host, PhysicalKey.ArrowUp, RawInputModifiers.Control);
        Assert.Equal(0, RowIndex(map));
    }

    /// <summary>
    /// The peer follows the selection: the focused square reports the keyboard focus, every
    /// square reports that it can take it, and moving the selection raises one property change
    /// rather than telling the client a year of children has been rebuilt.
    /// </summary>
    [AvaloniaFact]
    public void TheAutomationPeerFollowsTheFocusedSquare()
    {
        var map = new UsageMap { Rows = [Row("Claude", days: 28)] };
        using PixelHost host = PixelHost.Show(map, ThemeVariant.Light, width: 420d, height: 220d);

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(map);
        IReadOnlyList<AutomationPeer> squares = peer.GetChildren();

        Assert.NotEmpty(squares);
        Assert.All(squares, square => Assert.False(square.HasKeyboardFocus()));
        Assert.All(squares, square => Assert.True(square.IsKeyboardFocusable()));
        Assert.Equal(UsageMapAutomationPeer.MapName, peer.GetName());

        List<AutomationProperty> changed = [];
        peer.PropertyChanged += (_, e) => changed.Add(e.Property);

        Enter(map);

        AutomationPeer first = Assert.Single(squares, square => square.HasKeyboardFocus());
        Assert.Equal(UsageMapAutomationPeer.NameFor("Claude", map.Rows![0].Cells[0]), first.GetName());
        Assert.Equal(first.GetName(), peer.GetName());
        Assert.Contains(AutomationElementIdentifiers.NameProperty, changed);

        changed.Clear();
        Press(host, PhysicalKey.ArrowDown);

        AutomationPeer second = Assert.Single(squares, square => square.HasKeyboardFocus());
        Assert.Equal(UsageMapAutomationPeer.NameFor("Claude", map.Rows[0].Cells[1]), second.GetName());
        Assert.Contains(AutomationElementIdentifiers.NameProperty, changed);
    }

    /// <summary>
    /// Nothing known, nothing to focus - and the map given no rows at all still draws nothing.
    /// A focusable empty map is how the standing "an unset map draws nothing" assertion dies:
    /// a hosted window can hand focus to the only focusable control in it.
    /// </summary>
    [AvaloniaFact]
    public void AMapWithNothingKnownIsNotFocusableAndStillDrawsNothing()
    {
        var unset = new UsageMap();
        using (PixelHost host = PixelHost.Show(unset, ThemeVariant.Light, width: 240d, height: 120d))
        {
            Assert.False(unset.Focusable, "A map that was given nothing offered itself for focus.");
            Assert.False(unset.Focus(NavigationMethod.Tab), "A map that was given nothing took focus.");
            Assert.Null(unset.FocusedCell);

            Frame frame = host.Capture();
            Rect area = host.BoundsOf(unset);
            Color ground = Token(ThemeVariant.Light, "AltimSurfaceBrush");

            Assert.True(
                frame.Count(area, colour => !Ink.Near(colour, ground, 2)) == 0,
                $"A map that was given no rows drew something: {frame.Describe(area)}");
        }

        var empty = new UsageMap { Rows = [] };
        using (PixelHost.Show(empty, ThemeVariant.Light, width: 240d, height: 120d))
        {
            Assert.False(empty.Focusable, "A map with no days offered itself for focus.");
        }

        var unknown = new UsageMap { Rows = [Row("Claude", days: 14, unknownHead: 14)] };
        using (PixelHost.Show(unknown, ThemeVariant.Light, width: 320d, height: 160d))
        {
            Assert.False(unknown.Focusable, "A map whose every day is unknown offered itself for focus.");
            Assert.False(unknown.Focus(NavigationMethod.Tab), "A map of unknown days took focus.");
            Assert.Null(unknown.FocusedCell);
        }
    }

    /// <summary>
    /// Replacing the rows drops the selection rather than keeping a square from the picture
    /// that has just been thrown away.
    /// </summary>
    [AvaloniaFact]
    public void ReplacingTheRowsDropsTheFocusedSquare()
    {
        var map = new UsageMap { Rows = [Row("Claude", days: 14)] };
        using PixelHost host = PixelHost.Show(map, ThemeVariant.Light, width: 420d, height: 220d);

        Enter(map);
        Assert.NotNull(map.FocusedCell);

        map.Rows = [Row("Claude", days: 14, unknownHead: 14)];
        Dispatcher.UIThread.RunJobs();

        Assert.Null(map.FocusedCell);
        Assert.False(map.Focusable, "A map that lost every known day stayed focusable.");
        Assert.Null(ToolTip.GetTip(map));
    }

    /// <summary>Focuses the map the way Tab would, and proves it took.</summary>
    private static void Enter(UsageMap map)
    {
        Assert.True(map.Focusable, "The map does not accept focus.");
        Assert.True(map.Focus(NavigationMethod.Tab), "The map refused focus.");
        Dispatcher.UIThread.RunJobs();
        Assert.True(map.FocusedCell is not null, "The map took focus without focusing a square.");
    }

    /// <summary>Presses one key at the window and lets the change settle.</summary>
    private static void Press(
        PixelHost host,
        PhysicalKey key,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        host.Window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static UsageMapCell Cell(UsageMap map)
    {
        Assert.True(map.FocusedCell is not null, "No square is focused.");
        return map.FocusedCell!.Value.Cell;
    }

    private static DateOnly Day(UsageMap map) => Cell(map).Day;

    private static int RowIndex(UsageMap map)
    {
        Assert.True(map.FocusedCell is not null, "No square is focused.");
        return map.FocusedCell!.Value.RowIndex;
    }

    /// <summary>One row of consecutive days, with unknown days at either end if asked for.</summary>
    private static UsageMapRow Row(
        string name,
        int days,
        int unknownHead = 0,
        int unknownTail = 0,
        bool combined = false)
    {
        var cells = new UsageMapCell[days];
        for (int index = 0; index < days; index++)
        {
            DateOnly day = Start.AddDays(index);
            long tokens = (index + 1) * 1_000L;

            cells[index] = index < unknownHead || index >= days - unknownTail
                ? new UsageMapCell(day, null, null, IsKnown: false, $"{name} {day:yyyy-MM-dd}: no data")
                : new UsageMapCell(day, tokens, null, IsKnown: true,
                                   $"{name} {day:yyyy-MM-dd}: {tokens} tokens");
        }

        return new UsageMapRow(name, cells, combined);
    }

    /// <summary>The four bands a ring of one width occupies round a square.</summary>
    private static Rect[] Bands(Rect square, double width) =>
    [
        new(square.X, square.Y - width, square.Width, width),
        new(square.X, square.Bottom, square.Width, width),
        new(square.X - width, square.Y, width, square.Height),
        new(square.Right, square.Y, width, square.Height),
    ];

    /// <summary>The middle of a square, clear of its own edge.</summary>
    private static Rect Inside(Rect cell) =>
        new(cell.X + 2d, cell.Y + 2d, Math.Max(1d, cell.Width - 4d), Math.Max(1d, cell.Height - 4d));

    /// <summary>One square's rectangle in window coordinates.</summary>
    private static Rect Square(PixelHost host, UsageMap map, int row, DateOnly day)
    {
        Rect? local = map.CellBounds(row, day);
        Assert.True(local is not null, $"The map has no cell for {day:yyyy-MM-dd} in row {row}.");

        Rect origin = host.BoundsOf(map);
        return local!.Value.Translate(new Vector(origin.X, origin.Y));
    }

    /// <summary>The centre of one square, in window coordinates.</summary>
    private static Point Centre(UsageMap map, Point origin, int row, DateOnly day)
    {
        Rect? cell = map.CellBounds(row, day);
        Assert.True(cell is not null, $"The map has no cell for {day:yyyy-MM-dd} in row {row}.");
        return cell!.Value.Center + new Point(origin.X, origin.Y);
    }

    private static ThemeVariant Variant(string name) =>
        name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static Color Token(ThemeVariant variant, string key)
    {
        Assert.True(
            DesignSystem.Ensure().TryGetResource(key, variant, out object? value),
            $"{key} does not resolve under {variant}.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
