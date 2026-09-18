using Altim.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using CoreRect = Altim.Core.Models.PixelRect;

namespace Altim.UI.Tests;

/// <summary>
/// Where the tray panel's window goes, what it must never cover, and what it must never lose.
/// </summary>
/// <remarks>
/// <para>
/// The window is bigger than the panel, because the one shadow in the design system needs
/// transparent room to fall into. That room is still window: it hit tests, so wherever it
/// lands on top of the tray icon the shell never sees the click and the icon reads as
/// broken. It shipped that way — the panel stands 8 DIPs from the icon and reserved 32
/// below itself, so the window's bottom edge landed 24 DIPs past the icon's top and the
/// upper half of the icon stopped answering. Measured on a bottom taskbar at 100%: the
/// window owned y=1044 to y=1055 over an icon at y=1044, so a click on the lower half
/// toggled the panel and a click on the upper half did nothing at all.
/// </para>
/// <para>
/// So these are the two assertions most cases make: the window is clear of the anchor, and
/// the panel is exactly where it was before the inset was trimmed. The second is what makes
/// the first cheap — the trim only gives up shadow room the taskbar was covering anyway.
/// This is pure arithmetic over rectangles, so it needs no screen.
/// </para>
/// <para>
/// <b>The sizes are the ones the application can actually be.</b> This file used to pin the
/// panel at 374 DIPs tall and call it "two providers' worth", and every rectangle in it was
/// therefore computed against a size the panel is never in: the real panel measures 529 with
/// one provider, 659 with two and 765 with three, and the shipped application registers three
/// unconditionally. The tall cases are where the placement was broken — a 765 panel does not
/// fit a 1366x768 screen, or a 1920x1080 one at 150% — and a fixture 285 DIPs short of the
/// truth could not reach any of them. <see cref="ThePanelsTopEdgeIsNeverOffTheWorkingArea"/>
/// is the assertion that was missing with it.
/// </para>
/// </remarks>
public sealed class PopupPlacementTests
{
    /// <summary>The design system's inset: AltimPopupShadowMargin.</summary>
    private static readonly Thickness Design = new(24d, 16d, 24d, 32d);

    /// <summary>The panel with one provider, in DIPs, measured from the running application.</summary>
    private const double OneProvider = 529d;

    /// <summary>The panel with two providers, in DIPs, measured from the running application.</summary>
    private const double TwoProviders = 659d;

    /// <summary>
    /// The panel with three providers, in DIPs, measured from the running application, which
    /// is what the shipped application produces: it registers all three unconditionally.
    /// </summary>
    private const double ThreeProviders = 765d;

    /// <summary>The panel, in DIPs: the 320 from DESIGN.md and two providers' worth of height.</summary>
    private static readonly Size Panel = new(320d, TwoProviders);

    /// <summary>Every panel height the application can produce.</summary>
    private static readonly double[] Heights = [OneProvider, TwoProviders, ThreeProviders];

    /// <summary>
    /// The displays the panel has to open on: the small ones, the scaled ones, and two with
    /// room to spare so the cases that fit are exercised as well as the ones that do not.
    /// </summary>
    private static readonly Display[] Displays =
    [
        new("1366x768 at 100%", 1366, 768, 1d, 40),
        new("1920x1080 at 100%", 1920, 1080, 1d, 48),
        new("1600x900 at 125%", 1600, 900, 1.25d, 60),
        new("1920x1080 at 150%", 1920, 1080, 1.5d, 72),
        new("1920x1080 at 175%", 1920, 1080, 1.75d, 84),
        new("1920x1200 at 150%", 1920, 1200, 1.5d, 72),
        new("2560x1440 at 100%", 2560, 1440, 1d, 48),
        new("3840x2160 at 200%", 3840, 2160, 2d, 96),
    ];

    /// <summary>
    /// The window never covers the tray icon, whichever edge the taskbar is on.
    /// </summary>
    /// <param name="taskbar">The taskbar's edge, which is the panel edge facing the icon.</param>
    [Theory]
    [InlineData(PopupAnchorEdge.Bottom)]
    [InlineData(PopupAnchorEdge.Top)]
    [InlineData(PopupAnchorEdge.Left)]
    [InlineData(PopupAnchorEdge.Right)]
    public void TheWindowNeverCoversTheTrayIcon(PopupAnchorEdge taskbar)
    {
        Scenario scenario = For(taskbar);

        PopupPlacementResult placed = PopupPlacement.Compute(
            scenario.Anchor, cursor: null, scenario.WorkingArea, 1d, Panel, Design);

        AssertClearOf(scenario.Anchor, placed.Window);
    }

    /// <summary>
    /// The window and the panel inside it are at the pixel the placement is specified to put
    /// them at, on every taskbar edge. The panel is the part the user sees, so it carries the
    /// 8 DIP margin from the working area that DESIGN.md asks for; the window is the panel
    /// plus the inset, trimmed on the side facing the icon.
    /// </summary>
    /// <param name="taskbar">The taskbar's edge, which is the panel edge facing the icon.</param>
    [Theory]
    [InlineData(PopupAnchorEdge.Bottom)]
    [InlineData(PopupAnchorEdge.Top)]
    [InlineData(PopupAnchorEdge.Left)]
    [InlineData(PopupAnchorEdge.Right)]
    public void ThePanelIsWhereTheDesignPutsItAndTheWindowIsTrimmedToIt(PopupAnchorEdge taskbar)
    {
        Scenario scenario = For(taskbar);

        PopupPlacementResult placed = PopupPlacement.Compute(
            scenario.Anchor, cursor: null, scenario.WorkingArea, 1d, Panel, Design);

        Assert.Equal(scenario.Inset, placed.ShadowInset);
        Assert.Equal(scenario.Window, placed.Window);
        Assert.Equal(scenario.Panel, PanelOf(placed, 1d));
        Assert.Equal(new PixelPoint(scenario.Window.X, scenario.Window.Y), placed.Position);
    }

    /// <summary>
    /// Only the edge facing the icon is trimmed. The other three keep the design's inset,
    /// because their shadow is over the desktop where it can be seen.
    /// </summary>
    /// <param name="taskbar">The taskbar's edge, which is the panel edge facing the icon.</param>
    [Theory]
    [InlineData(PopupAnchorEdge.Bottom)]
    [InlineData(PopupAnchorEdge.Top)]
    [InlineData(PopupAnchorEdge.Left)]
    [InlineData(PopupAnchorEdge.Right)]
    public void OnlyTheEdgeFacingTheIconIsTrimmed(PopupAnchorEdge taskbar)
    {
        Scenario scenario = For(taskbar);

        Thickness inset = PopupPlacement.Compute(
            scenario.Anchor, cursor: null, scenario.WorkingArea, 1d, Panel, Design).ShadowInset;

        Assert.Equal(taskbar is PopupAnchorEdge.Left ? PopupPlacement.Gap : Design.Left, inset.Left);
        Assert.Equal(taskbar is PopupAnchorEdge.Top ? PopupPlacement.Gap : Design.Top, inset.Top);
        Assert.Equal(taskbar is PopupAnchorEdge.Right ? PopupPlacement.Gap : Design.Right, inset.Right);
        Assert.Equal(taskbar is PopupAnchorEdge.Bottom ? PopupPlacement.Gap : Design.Bottom, inset.Bottom);
    }

    /// <summary>
    /// <b>The panel never loses its top edge.</b> On every display, every scaling, every
    /// taskbar edge and every height the panel can be, the panel's top is inside the working
    /// area — and it is at the top of it, not somewhere in the middle, because a panel that
    /// cannot fit gives up its bottom.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion the file was missing, and without it the shipped application put
    /// the panel's header off the top of the screen on five of the eight displays below. The
    /// clamp preferred the working area's top edge, correctly; the push that takes the window
    /// off the tray icon then ran afterwards, found an over-tall window overlapping the icon,
    /// and moved the whole window up by the whole overlap. On a 1920x1080 display at 150% with
    /// three providers that was 99 DIPs above the working area: the wordmark, the settings
    /// gear and the head of the dial, none of them reachable by any means.
    /// </para>
    /// <para>
    /// Where the panel does fit, the stronger property holds and is asserted: the whole panel
    /// is inside the working area and the window is clear of the icon.
    /// </para>
    /// </remarks>
    /// <param name="taskbar">The taskbar's edge, which is the panel edge facing the icon.</param>
    [Theory]
    [InlineData(PopupAnchorEdge.Bottom)]
    [InlineData(PopupAnchorEdge.Top)]
    [InlineData(PopupAnchorEdge.Left)]
    [InlineData(PopupAnchorEdge.Right)]
    public void ThePanelsTopEdgeIsNeverOffTheWorkingArea(PopupAnchorEdge taskbar)
    {
        foreach (Display display in Displays)
        {
            (PixelRect workingArea, CoreRect icon) = Layout(display, taskbar);

            foreach (double height in Heights)
            {
                PopupPlacementResult placed = PopupPlacement.Compute(
                    icon, cursor: null, workingArea, display.Scaling, new Size(320d, height), Design);

                PixelRect panel = PanelOf(placed, display.Scaling);
                string where = $"{display.Name}, {taskbar} taskbar, a {height} DIP panel: "
                    + $"working area {workingArea}, panel {panel}";

                int margin = (int)Math.Round(PopupPlacement.EdgeMargin * display.Scaling);

                Assert.True(
                    panel.Y >= workingArea.Y,
                    $"The panel's top is {workingArea.Y - panel.Y}px above the working area. {where}");
                Assert.True(
                    panel.X >= workingArea.X && panel.Right <= workingArea.Right,
                    $"The panel is off the working area sideways. {where}");

                if (Fits(height, workingArea, display.Scaling))
                {
                    Assert.True(
                        panel.Bottom <= workingArea.Bottom,
                        $"The panel fits the working area and is still hanging off the bottom. {where}");
                    AssertClearOf(icon, placed.Window);
                    continue;
                }

                // Too tall to place. It is at the top of the working area rather than
                // somewhere down it, which is what "loses its bottom, not its top" means:
                // the only thing it may give up above itself is the 8 DIP margin.
                Assert.True(
                    panel.Y <= workingArea.Y + margin,
                    $"The panel is parked {panel.Y - workingArea.Y}px down the working area rather"
                        + $" than at the top of it, so it is losing its top before its bottom. {where}");
            }
        }
    }

    /// <summary>
    /// The case that shipped broken, as the machine reported it: a 24px icon in a 48px
    /// bottom taskbar on a 1920x1080 display at 100%. The window used to run past the working
    /// area and own the icon's top half; it now stops at the working area's edge.
    /// </summary>
    [Fact]
    public void TheMeasuredBottomTaskbarCaseClearsTheIcon()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1032);
        var icon = new CoreRect(1670, 1044, 24, 24);

        PopupPlacementResult placed = PopupPlacement.Compute(
            icon, cursor: null, workingArea, 1d, Panel, Design);

        // The window, as WindowFromPoint reports it.
        Assert.Equal(new PixelRect(1498, 349, 368, 683), placed.Window);

        // Its bottom edge is the working area's, so every pixel of the taskbar is the
        // shell's. The panel is untouched: 8 DIPs clear of the working area's edge.
        Assert.Equal(workingArea.Bottom, placed.Window.Bottom);
        Assert.Equal(new PixelRect(1522, 365, 320, 659), PanelOf(placed, 1d));
        AssertClearOf(icon, placed.Window);
    }

    /// <summary>
    /// The margins are in device-independent units, so they grow with the display. At 150%
    /// the trimmed inset is 12 physical pixels rather than 8 and the window still stops at
    /// the working area's edge.
    /// </summary>
    [Fact]
    public void TheTrimScalesWithTheDisplay()
    {
        var workingArea = new PixelRect(0, 0, 2880, 1548);
        var icon = new CoreRect(2500, 1566, 36, 36);

        PopupPlacementResult placed = PopupPlacement.Compute(
            icon, cursor: null, workingArea, 1.5d, Panel, Design);

        Assert.Equal(new Thickness(24d, 16d, 24d, PopupPlacement.Gap), placed.ShadowInset);
        Assert.Equal(new PixelRect(2242, 523, 552, 1025), placed.Window);
        Assert.Equal(workingArea.Bottom, placed.Window.Bottom);
        AssertClearOf(icon, placed.Window);
    }

    /// <summary>
    /// The second tier is the cursor, as a one-pixel rectangle, and the window clears that
    /// too: the pixel under the click is where the icon the user pressed is.
    /// </summary>
    [Fact]
    public void TheCursorFallbackIsClearedLikeAnIcon()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1032);
        var cursor = new PixelPoint(1682, 1050);

        PopupPlacementResult placed = PopupPlacement.Compute(
            anchor: null, cursor, workingArea, 1d, Panel, Design);

        Assert.Equal(new Thickness(24d, 16d, 24d, PopupPlacement.Gap), placed.ShadowInset);
        Assert.Equal(workingArea.Bottom, placed.Window.Bottom);
        AssertClearOf(new CoreRect(cursor.X, cursor.Y, 1, 1), placed.Window);
    }

    /// <summary>
    /// With no anchor at all — a Linux tray click, or an icon in the Windows 11 overflow —
    /// the panel takes the working area's corner and the window keeps the whole inset. There
    /// is nothing to clear, so there is no reason to give up any of the shadow.
    /// </summary>
    [Fact]
    public void TheCornerFallbackKeepsTheWholeInset()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1032);

        PopupPlacementResult placed = PopupPlacement.Compute(
            anchor: null, cursor: null, workingArea, 1d, Panel, Design);

        Assert.Equal(Design, placed.ShadowInset);
        Assert.Equal(new PixelRect(1592, 365, 320, 659), PanelOf(placed, 1d));
        Assert.Equal(new PixelRect(1568, 349, 368, 707), placed.Window);
    }

    /// <summary>
    /// An empty anchor rectangle is not a rectangle at the origin: it falls through to the
    /// cursor, and then to the corner.
    /// </summary>
    [Fact]
    public void AnEmptyAnchorFallsThroughToTheNextTier()
    {
        var cursor = new PixelPoint(1682, 1050);

        Assert.Equal(
            new PixelRect(1682, 1050, 1, 1),
            PopupPlacement.Resolve(new CoreRect(1670, 1044, 0, 0), cursor));

        Assert.Equal(default, PopupPlacement.Resolve(anchor: null, cursor: null));
    }

    /// <summary>
    /// A panel with nowhere to go covers the tray icon rather than losing its header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only case where the window is allowed over the icon, and it is the case
    /// where every other option is worse. A working area too small to hold the panel clamps
    /// the panel onto the icon, and the push that takes the window back off it has to move
    /// the window by the whole overlap — which, unbounded, carried the panel's top edge with
    /// it. The old form of this test asserted the window cleared the icon and was 398 tall,
    /// and passed while the panel's top sat 355px above the screen.
    /// </para>
    /// <para>
    /// The ranking is: the panel's bottom goes first, then its margin, then the tray icon,
    /// and its top never goes. An icon that ignores half its clicks is a bad defect that the
    /// user can work around by clicking the other half; a panel whose header, settings gear
    /// and dial are off the top of the screen has no workaround at all. The real fix is one
    /// level up — the window is capped so a panel this tall never reaches here — and this is
    /// what the arithmetic does when it is handed one anyway.
    /// </para>
    /// </remarks>
    [Fact]
    public void APanelWithNowhereToGoCoversTheIconRatherThanLosingItsHeader()
    {
        var workingArea = new PixelRect(0, 0, 1920, 300);
        var icon = new CoreRect(1670, 312, 24, 24);

        PopupPlacementResult placed = PopupPlacement.Compute(
            icon, cursor: null, workingArea, 1d, Panel, Design);

        PixelRect panel = PanelOf(placed, 1d);

        // The whole point: the panel's first row is on the screen.
        Assert.Equal(workingArea.Y, panel.Y);
        Assert.Equal(new PixelRect(1522, 0, 320, 659), panel);

        // The push still ran, and still took what it could: without it the window's bottom
        // would be at 675 rather than 667, and it has given up its 8px margin to do it.
        Assert.Equal(new PixelRect(1498, -16, 368, 683), placed.Window);

        // And this is the price, stated rather than discovered: the window is over the icon,
        // because the alternative was a panel with no top.
        Assert.True(
            placed.Window.Bottom > icon.Y,
            "The window cleared the icon, which on a panel this tall can only mean it left the screen.");
    }

    /// <summary>A scaling of zero is a screen that did not report one, and never a divide.</summary>
    [Fact]
    public void ABrokenScalingIsTreatedAsOneToOne()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1032);
        var icon = new CoreRect(1670, 1044, 24, 24);

        Assert.Equal(
            PopupPlacement.Compute(icon, cursor: null, workingArea, 1d, Panel, Design),
            PopupPlacement.Compute(icon, cursor: null, workingArea, 0d, Panel, Design));

        Assert.Equal(
            PopupPlacement.MaxWindowHeight(workingArea, 1d, Design),
            PopupPlacement.MaxWindowHeight(workingArea, 0d, Design));
    }

    /// <summary>
    /// The ceiling the window takes: the working area in device-independent units, less the
    /// panel's margin either side, plus the inset the window reserves around the panel.
    /// </summary>
    /// <remarks>
    /// It is in DIPs and computed from the target screen's scaling, so the same physical
    /// screen at two scalings gives two different ceilings and the same logical screen gives
    /// one. That is what makes 175% the worst case rather than the largest screen.
    /// </remarks>
    [Fact]
    public void TheWindowsCeilingIsWhatTheWorkingAreaHoldsInDips()
    {
        Thickness trimmed = new(24d, 16d, 24d, PopupPlacement.Gap);

        // 1032 physical pixels at 100% is 1032 DIPs: 1016 for the panel, plus 16 above it
        // and the trimmed 8 below.
        Assert.Equal(1040d, PopupPlacement.MaxWindowHeight(new PixelRect(0, 0, 1920, 1032), 1d, trimmed));

        // The same logical screen at 150% is 1548 physical pixels and the same 1040 DIPs.
        Assert.Equal(1040d, PopupPlacement.MaxWindowHeight(new PixelRect(0, 0, 2880, 1548), 1.5d, trimmed));

        // A screen with no room at all still gives a positive height rather than a negative
        // one that would arrive at the window as a throw.
        Assert.True(PopupPlacement.MaxWindowHeight(new PixelRect(0, 0, 320, 8), 1d, trimmed) > 0d);
    }

    /// <summary>
    /// The ceiling is below the panel on the displays the defect was measured on, and above
    /// it on the ones with room. A ceiling that never binds would leave the panel exactly as
    /// broken as it was while every other test here passed.
    /// </summary>
    [Fact]
    public void TheCeilingBindsOnTheDisplaysThePanelDoesNotFit()
    {
        Thickness trimmed = new(24d, 16d, 24d, PopupPlacement.Gap);
        var binds = new List<string>();

        foreach (Display display in Displays)
        {
            (PixelRect workingArea, _) = Layout(display, PopupAnchorEdge.Bottom);

            double ceiling = PopupPlacement.MaxWindowHeight(workingArea, display.Scaling, trimmed);
            double panel = ceiling - trimmed.Top - trimmed.Bottom;

            Assert.Equal(Fits(ThreeProviders, workingArea, display.Scaling), ThreeProviders <= panel);

            if (ThreeProviders > panel)
            {
                binds.Add(display.Name);
            }
        }

        Assert.Equal(
            ["1366x768 at 100%", "1600x900 at 125%", "1920x1080 at 150%", "1920x1080 at 175%", "1920x1200 at 150%"],
            binds);
    }

    /// <summary>
    /// A panel taller than its ceiling scrolls. The placement can only choose which end of a
    /// panel that does not fit is lost, so the window is capped and the panel is given a way
    /// to reach the rest of itself; without the scroll viewer the cap would simply clip the
    /// bottom off and nothing would bring it back.
    /// </summary>
    [AvaloniaFact]
    public void APanelTallerThanItsCeilingScrollsRatherThanLosingItsBottom()
    {
        // The three-provider panel on a 1366x768 screen with a 40px taskbar: 728 DIPs of
        // working area against 765 DIPs of panel.
        var workingArea = new PixelRect(0, 0, 1366, 728);
        Thickness trimmed = new(24d, 16d, 24d, PopupPlacement.Gap);

        var content = new Border { Height = ThreeProviders };
        Window window = DesignSystem.PopupWindow(transparent: true, content: content);
        window.MaxHeight = PopupPlacement.MaxWindowHeight(workingArea, 1d, trimmed);

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 728 less the panel's 8 either side, plus the inset: 736, where the panel and
            // the whole inset would have wanted 765 + 16 + 32.
            Assert.Equal(736d, window.MaxHeight);
            Assert.Equal(736d, window.Bounds.Height, 3);

            ScrollViewer scroller = Assert.Single(window.GetVisualDescendants().OfType<ScrollViewer>());

            Assert.True(
                scroller.Extent.Height > scroller.Viewport.Height,
                $"The panel is not scrollable: extent {scroller.Extent}, viewport {scroller.Viewport}.");
            Assert.True(
                scroller.ScrollBarMaximum.Y > 0d,
                "There is nothing to scroll to, so the bottom of the panel cannot be reached.");

            // And it opens at the top, which is the edge the placement is protecting.
            Assert.Equal(0d, scroller.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A panel that fits its ceiling is untouched: no scrollbar, and the window is exactly as
    /// tall as the panel and its inset. The cap must not cost anything on the screens where
    /// there was never a problem.
    /// </summary>
    [AvaloniaFact]
    public void APanelInsideItsCeilingIsNotScrolled()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1032);
        Thickness trimmed = new(24d, 16d, 24d, PopupPlacement.Gap);

        var content = new Border { Height = ThreeProviders };
        Window window = DesignSystem.PopupWindow(transparent: true, content: content);
        window.MaxHeight = PopupPlacement.MaxWindowHeight(workingArea, 1d, trimmed);

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // The panel plus the whole design inset and the panel's own 1px border top and
            // bottom, which is what it was before any of this.
            Assert.Equal(ThreeProviders + Design.Top + Design.Bottom + 2d, window.Bounds.Height, 3);
            Assert.True(window.Bounds.Height < window.MaxHeight, "The ceiling bound a panel that fits.");

            ScrollViewer scroller = Assert.Single(window.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.Equal(0d, scroller.ScrollBarMaximum.Y);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Whether a panel of a height fits a working area with its margins.</summary>
    /// <param name="height">The panel's height in DIPs.</param>
    /// <param name="workingArea">The working area in physical pixels.</param>
    /// <param name="scaling">The screen's scaling.</param>
    /// <returns>True when it fits.</returns>
    private static bool Fits(double height, PixelRect workingArea, double scaling) =>
        (height + (2d * PopupPlacement.EdgeMargin)) * scaling <= workingArea.Height;

    /// <summary>The window shares no pixel with the anchor.</summary>
    /// <param name="anchor">The tray icon's rectangle.</param>
    /// <param name="window">The window rectangle.</param>
    private static void AssertClearOf(CoreRect anchor, PixelRect window)
    {
        bool covers = window.X < anchor.Right
            && anchor.X < window.Right
            && window.Y < anchor.Bottom
            && anchor.Y < window.Bottom;

        Assert.False(
            covers,
            $"The window {window} covers the tray icon ({anchor.X},{anchor.Y},{anchor.Width},{anchor.Height}),"
                + " so the shell never sees a click there.");
    }

    /// <summary>The panel inside a placed window: the window less the inset it reserved.</summary>
    /// <param name="placed">The placement.</param>
    /// <param name="scaling">The screen's scaling.</param>
    /// <returns>The panel's rectangle in physical pixels.</returns>
    private static PixelRect PanelOf(PopupPlacementResult placed, double scaling)
    {
        int left = (int)Math.Round(placed.ShadowInset.Left * scaling, MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(placed.ShadowInset.Top * scaling, MidpointRounding.AwayFromZero);
        int right = (int)Math.Round(placed.ShadowInset.Right * scaling, MidpointRounding.AwayFromZero);
        int bottom = (int)Math.Round(placed.ShadowInset.Bottom * scaling, MidpointRounding.AwayFromZero);

        return new PixelRect(
            placed.Window.X + left,
            placed.Window.Y + top,
            placed.Window.Width - left - right,
            placed.Window.Height - top - bottom);
    }

    /// <summary>
    /// A display's working area and the tray icon inside its taskbar, for one taskbar edge.
    /// </summary>
    /// <remarks>
    /// The icon is a 24 DIP square centred across the taskbar's thickness, four taskbar
    /// widths in from the corner, which is where a shell puts the notification area.
    /// </remarks>
    /// <param name="display">The display.</param>
    /// <param name="taskbar">The edge the taskbar is on.</param>
    /// <returns>The working area and the icon, in physical pixels.</returns>
    private static (PixelRect WorkingArea, CoreRect Icon) Layout(Display display, PopupAnchorEdge taskbar)
    {
        int bar = display.Taskbar;
        int mark = (int)Math.Round(24d * display.Scaling);
        int pad = Math.Max(0, (bar - mark) / 2);
        int along = bar * 4;

        return taskbar switch
        {
            PopupAnchorEdge.Bottom => (
                new PixelRect(0, 0, display.Width, display.Height - bar),
                new CoreRect(display.Width - along, display.Height - bar + pad, mark, mark)),

            PopupAnchorEdge.Top => (
                new PixelRect(0, bar, display.Width, display.Height - bar),
                new CoreRect(display.Width - along, pad, mark, mark)),

            PopupAnchorEdge.Left => (
                new PixelRect(bar, 0, display.Width - bar, display.Height),
                new CoreRect(pad, display.Height - along, mark, mark)),

            _ => (
                new PixelRect(0, 0, display.Width - bar, display.Height),
                new CoreRect(display.Width - bar + pad, display.Height - along, mark, mark)),
        };
    }

    /// <summary>
    /// One display, one taskbar edge, and what the placement must produce on it. The screen
    /// is 1920x1080 with a 48px taskbar, and the icon is the 24px square the shell reports
    /// inside it.
    /// </summary>
    /// <param name="taskbar">The edge the taskbar is on.</param>
    /// <returns>The scenario.</returns>
    private static Scenario For(PopupAnchorEdge taskbar) => taskbar switch
    {
        PopupAnchorEdge.Bottom => new Scenario(
            new PixelRect(0, 0, 1920, 1032),
            new CoreRect(1670, 1044, 24, 24),
            new PixelRect(1522, 365, 320, 659),
            new PixelRect(1498, 349, 368, 683),
            new Thickness(24d, 16d, 24d, 8d)),

        PopupAnchorEdge.Top => new Scenario(
            new PixelRect(0, 48, 1920, 1032),
            new CoreRect(1670, 12, 24, 24),
            new PixelRect(1522, 56, 320, 659),
            new PixelRect(1498, 48, 368, 699),
            new Thickness(24d, 8d, 24d, 32d)),

        PopupAnchorEdge.Left => new Scenario(
            new PixelRect(48, 0, 1872, 1080),
            new CoreRect(12, 900, 24, 24),
            new PixelRect(56, 413, 320, 659),
            new PixelRect(48, 397, 352, 707),
            new Thickness(8d, 16d, 24d, 32d)),

        _ => new Scenario(
            new PixelRect(0, 0, 1872, 1080),
            new CoreRect(1884, 900, 24, 24),
            new PixelRect(1544, 413, 320, 659),
            new PixelRect(1520, 397, 352, 707),
            new Thickness(24d, 16d, 8d, 32d)),
    };

    /// <summary>One taskbar arrangement and the placement it must produce.</summary>
    /// <param name="WorkingArea">The screen's working area in physical pixels.</param>
    /// <param name="Anchor">The tray icon's rectangle in physical pixels.</param>
    /// <param name="Panel">Where the panel must land.</param>
    /// <param name="Window">Where the window must land.</param>
    /// <param name="Inset">The inset the window must reserve.</param>
    private sealed record Scenario(
        PixelRect WorkingArea,
        CoreRect Anchor,
        PixelRect Panel,
        PixelRect Window,
        Thickness Inset);

    /// <summary>One screen the panel has to open on.</summary>
    /// <param name="Name">What to call it when an assertion fails.</param>
    /// <param name="Width">Its width in physical pixels.</param>
    /// <param name="Height">Its height in physical pixels.</param>
    /// <param name="Scaling">Its scaling factor.</param>
    /// <param name="Taskbar">The taskbar's thickness in physical pixels.</param>
    private sealed record Display(string Name, int Width, int Height, double Scaling, int Taskbar);
}
