using Altim.UI.Views;
using Avalonia;
using Xunit;
using CoreRect = Altim.Core.Models.PixelRect;

namespace Altim.UI.Tests;

/// <summary>
/// Where the tray panel's window goes, and what it must never cover.
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
/// So these are the two assertions every case makes: the window is clear of the anchor, and
/// the panel is exactly where it was before the inset was trimmed. The second is what makes
/// the first cheap — the trim only gives up shadow room the taskbar was covering anyway.
/// This is pure arithmetic over rectangles, so it needs no screen; the sizes below are the
/// real ones, measured from the running application on a 1920x1080 display.
/// </para>
/// </remarks>
public sealed class PopupPlacementTests
{
    /// <summary>The design system's inset: AltimPopupShadowMargin.</summary>
    private static readonly Thickness Design = new(24d, 16d, 24d, 32d);

    /// <summary>The panel, in DIPs: the 320 from DESIGN.md and two providers' worth of height.</summary>
    private static readonly Size Panel = new(320d, 374d);

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
    /// The case that shipped broken, as the machine reported it: a 24px icon in a 48px
    /// bottom taskbar on a 1920x1080 display at 100%. The window used to run to y=1056 and
    /// own the icon's top half; it now stops at the working area's edge, 12px clear of it.
    /// </summary>
    [Fact]
    public void TheMeasuredBottomTaskbarCaseClearsTheIcon()
    {
        var workingArea = new PixelRect(0, 0, 1920, 1032);
        var icon = new CoreRect(1670, 1044, 24, 24);

        PopupPlacementResult placed = PopupPlacement.Compute(
            icon, cursor: null, workingArea, 1d, Panel, Design);

        // The window, as WindowFromPoint reports it.
        Assert.Equal(new PixelRect(1498, 634, 368, 398), placed.Window);

        // Its bottom edge is the working area's, so every pixel of the taskbar is the
        // shell's. The panel is untouched: 8 DIPs clear of the working area's edge.
        Assert.Equal(workingArea.Bottom, placed.Window.Bottom);
        Assert.Equal(new PixelRect(1522, 650, 320, 374), PanelOf(placed, 1d));
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
        Assert.Equal(new PixelRect(2242, 951, 552, 597), placed.Window);
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
        Assert.Equal(new PixelRect(1592, 650, 320, 374), PanelOf(placed, 1d));
        Assert.Equal(new PixelRect(1568, 634, 368, 422), placed.Window);
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
    /// When the trim is not enough the panel moves. A working area too small to hold the
    /// panel and its margins clamps the panel towards the icon, and no inset at all would
    /// keep the window off it, so the remainder comes off the panel's position instead: a
    /// panel a few pixels out of place beats an icon that ignores half its clicks.
    /// </summary>
    [Fact]
    public void APanelClampedOntoTheIconIsPushedOffIt()
    {
        var workingArea = new PixelRect(0, 0, 1920, 300);
        var icon = new CoreRect(1670, 312, 24, 24);

        PopupPlacementResult placed = PopupPlacement.Compute(
            icon, cursor: null, workingArea, 1d, Panel, Design);

        // Without the push the window would run from y=-8 to y=390, over an icon at 312.
        Assert.Equal(icon.Y, placed.Window.Bottom);
        Assert.Equal(398, placed.Window.Height);
        AssertClearOf(icon, placed.Window);
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
    }

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
            new PixelRect(1522, 650, 320, 374),
            new PixelRect(1498, 634, 368, 398),
            new Thickness(24d, 16d, 24d, 8d)),

        PopupAnchorEdge.Top => new Scenario(
            new PixelRect(0, 48, 1920, 1032),
            new CoreRect(1670, 12, 24, 24),
            new PixelRect(1522, 56, 320, 374),
            new PixelRect(1498, 48, 368, 414),
            new Thickness(24d, 8d, 24d, 32d)),

        PopupAnchorEdge.Left => new Scenario(
            new PixelRect(48, 0, 1872, 1080),
            new CoreRect(12, 900, 24, 24),
            new PixelRect(56, 698, 320, 374),
            new PixelRect(48, 682, 352, 422),
            new Thickness(8d, 16d, 24d, 32d)),

        _ => new Scenario(
            new PixelRect(0, 0, 1872, 1080),
            new CoreRect(1884, 900, 24, 24),
            new PixelRect(1544, 698, 320, 374),
            new PixelRect(1520, 682, 352, 422),
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
}
