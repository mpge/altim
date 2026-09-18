using Avalonia;
using CoreRect = Altim.Core.Models.PixelRect;

namespace Altim.UI.Views;

/// <summary>
/// Where the popup window goes, as arithmetic with no Avalonia window in it.
/// </summary>
/// <remarks>
/// <para>
/// Four things make this harder than it looks, and all four are handled here.
/// </para>
/// <para>
/// <b>Units.</b> Screen coordinates are physical pixels and window sizes are
/// device-independent units. Every conversion uses the <em>target</em> screen's scaling, not
/// the primary screen's and not the popup's current one, because the popup may be about to
/// move from a 100% monitor to a 150% one. The margins scale too: an 8pt gap that stays 8
/// physical pixels is invisible at 200%.
/// </para>
/// <para>
/// <b>The shadow.</b> The window is wider and taller than the panel the user sees, because
/// the one shadow in the design system needs transparent room to fall into. The panel is
/// what should line up with the tray icon, so the caller passes the <em>panel</em> size and
/// gets back a window rectangle: the panel grown by the shadow inset.
/// </para>
/// <para>
/// <b>Which edge.</b> The taskbar is not always at the bottom. The anchor's nearest
/// working-area edge is taken as the taskbar edge and the panel is placed on the opposite
/// side of the anchor along that axis, then clamped into the working area, so a panel next
/// to a left-hand taskbar opens to its right rather than off the screen.
/// </para>
/// <para>
/// <b>The icon underneath.</b> That transparent room is still window, and a window over a
/// tray icon eats the clicks meant for it. The panel stands <see cref="Gap"/> from the icon
/// while the inset on the edge facing it is 32 — so the window's edge landed 24 DIPs
/// <em>past</em> the icon's near edge and swallowed every click on that half of it. So the
/// inset on the anchored edge is trimmed to the gap: the room that is left is the room the
/// shadow can actually be seen in, because past the panel's near edge the taskbar covers it.
/// The returned inset is the trimmed one and the caller must apply it to the window's
/// chrome, or the window will be the size this arithmetic did not assume. Anything still
/// over the icon after that — which takes a panel clamped by a working area too small to
/// hold it — is pushed off it, because a panel a few pixels out of place is a smaller defect
/// than a tray icon that does not answer.
/// </para>
/// <para>
/// <b>The top edge.</b> That push has a floor, and there is a height cap above it. A panel
/// taller than the working area cannot be placed correctly at all, so the two rules that
/// give way are ranked rather than left to disagree: the panel's bottom goes first, then the
/// margin, then the tray icon, and its <em>top</em> never goes. The top is the wordmark, the
/// settings gear and the head of the dial, and a panel that has lost them has lost the
/// things that say what it is and how to leave it. <see cref="MaxWindowHeight"/> is the
/// other half of it: the window is capped to what the working area holds and scrolls inside
/// that, so a panel this tall does not arrive here in the first place.
/// </para>
/// </remarks>
public static class PopupPlacement
{
    /// <summary>Distance between the tray icon and the panel, in device-independent units.</summary>
    public const double Gap = 8;

    /// <summary>Smallest distance between the panel and the working area's edge, in DIPs.</summary>
    public const double EdgeMargin = 8;

    /// <summary>
    /// Computes the window rectangle and the shadow inset that rectangle was built from.
    /// </summary>
    /// <param name="anchor">
    /// The tray icon's rectangle in physical pixels, or null when the host could not report
    /// one, which is the case for an icon inside the Windows 11 overflow.
    /// </param>
    /// <param name="cursor">
    /// The cursor in physical pixels, the second tier, or null when it is not known either.
    /// </param>
    /// <param name="workingArea">The target screen's working area in physical pixels.</param>
    /// <param name="scaling">The target screen's scaling factor.</param>
    /// <param name="panelSize">The panel's size in device-independent units, without the inset.</param>
    /// <param name="shadowInset">The design system's transparent margin around the panel, in DIPs.</param>
    /// <returns>
    /// The window rectangle, chosen so that the <em>panel</em>, not the window, sits beside
    /// the anchor and inside the working area, and so that no part of the window covers the
    /// anchor; and the inset it was computed with, which is the design inset trimmed on the
    /// edge facing the anchor.
    /// </returns>
    public static PopupPlacementResult Compute(
        CoreRect? anchor,
        PixelPoint? cursor,
        PixelRect workingArea,
        double scaling,
        Size panelSize,
        Thickness shadowInset)
    {
        double scale = scaling > 0 ? scaling : 1d;

        int panelWidth = Math.Max(1, Scale(panelSize.Width, scale));
        int panelHeight = Math.Max(1, Scale(panelSize.Height, scale));

        int gap = Scale(Gap, scale);
        int edge = Scale(EdgeMargin, scale);

        PixelRect target = Resolve(anchor, cursor);
        PopupAnchorEdge facing = Facing(target, workingArea);
        Thickness inset = Trim(shadowInset, facing);

        PixelPoint panel = facing is PopupAnchorEdge.None
            ? Corner(workingArea, panelWidth, panelHeight, edge)
            : Beside(target, facing, panelWidth, panelHeight, gap);

        int x = Clamp(panel.X, workingArea.X + edge, workingArea.Right - edge - panelWidth);
        int y = Clamp(panel.Y, workingArea.Y + edge, workingArea.Bottom - edge - panelHeight);

        int left = Scale(inset.Left, scale);
        int top = Scale(inset.Top, scale);

        var window = new PixelRect(
            x - left,
            y - top,
            panelWidth + left + Scale(inset.Right, scale),
            panelHeight + top + Scale(inset.Bottom, scale));

        return new PopupPlacementResult(Clear(window, target, facing, workingArea, left, top), inset);
    }

    /// <summary>
    /// The tallest the popup window may be on a screen, in device-independent units, so that
    /// the panel inside it fits the working area with the margin DESIGN.md asks for.
    /// </summary>
    /// <remarks>
    /// The panel grows with the number of providers — 529 DIPs for one, 659 for two, 765 for
    /// three, measured — and small screens at high scalings leave less room than that. The
    /// window therefore carries a maximum and scrolls what does not fit, rather than opening
    /// taller than the screen and losing whichever end the arithmetic gives up. Without it
    /// the panel is placed correctly and is still unreadable, because the part hanging off
    /// the bottom can be neither seen nor reached.
    /// </remarks>
    /// <param name="workingArea">The target screen's working area in physical pixels.</param>
    /// <param name="scaling">The target screen's scaling factor.</param>
    /// <param name="shadowInset">
    /// The inset the window is carrying, in DIPs, which is the placement's trimmed one rather
    /// than the design's: the cap is on the window and the margin is on the panel inside it.
    /// </param>
    /// <returns>The window's maximum height in device-independent units.</returns>
    public static double MaxWindowHeight(PixelRect workingArea, double scaling, Thickness shadowInset)
    {
        double scale = scaling > 0 ? scaling : 1d;
        double panel = (workingArea.Height / scale) - (2d * EdgeMargin);

        return Math.Max(1d, panel) + shadowInset.Top + shadowInset.Bottom;
    }

    /// <summary>
    /// The anchor tiers, in order: the icon rectangle, the cursor, then nothing.
    /// </summary>
    /// <param name="anchor">The icon rectangle, or null.</param>
    /// <param name="cursor">The cursor, or null.</param>
    /// <returns>
    /// A rectangle to place beside, or an empty one meaning "fall to the working area
    /// corner". A null anchor is never read as a rectangle at the screen origin.
    /// </returns>
    public static PixelRect Resolve(CoreRect? anchor, PixelPoint? cursor)
    {
        if (anchor is { } rect && !rect.IsEmpty)
        {
            return new PixelRect(rect.X, rect.Y, rect.Width, rect.Height);
        }

        return cursor is { } point ? new PixelRect(point.X, point.Y, 1, 1) : default;
    }

    /// <summary>
    /// Which edge of the panel faces the anchor, which is the opposite side of the anchor
    /// from the taskbar it sits in.
    /// </summary>
    /// <param name="anchor">The anchor rectangle, which may be empty.</param>
    /// <param name="workingArea">The working area the taskbar edge is deduced from.</param>
    /// <returns>The panel edge that faces the anchor, or None when there is no anchor.</returns>
    private static PopupAnchorEdge Facing(PixelRect anchor, PixelRect workingArea)
    {
        if (anchor.Width <= 0 || anchor.Height <= 0)
        {
            return PopupAnchorEdge.None;
        }

        int centreX = anchor.X + (anchor.Width / 2);
        int centreY = anchor.Y + (anchor.Height / 2);

        int fromLeft = centreX - workingArea.X;
        int fromRight = workingArea.Right - centreX;
        int fromTop = centreY - workingArea.Y;
        int fromBottom = workingArea.Bottom - centreY;

        int nearest = Math.Min(Math.Min(fromLeft, fromRight), Math.Min(fromTop, fromBottom));

        // Vertical edges first: a left or right taskbar puts the icon far closer to that
        // edge than to the top or bottom, and the panel has to open sideways.
        if (nearest == fromLeft)
        {
            return PopupAnchorEdge.Left;
        }

        if (nearest == fromRight)
        {
            return PopupAnchorEdge.Right;
        }

        return nearest == fromTop ? PopupAnchorEdge.Top : PopupAnchorEdge.Bottom;
    }

    /// <summary>
    /// Shrinks the inset on the edge facing the anchor to the gap the panel already keeps
    /// from it, which is the only part of that side's shadow anything can see: past the
    /// panel's near edge the taskbar the icon sits in covers it.
    /// </summary>
    /// <param name="inset">The design system's inset.</param>
    /// <param name="facing">The panel edge that faces the anchor.</param>
    /// <returns>The inset to build the window from.</returns>
    private static Thickness Trim(Thickness inset, PopupAnchorEdge facing) => facing switch
    {
        PopupAnchorEdge.Left => new Thickness(Math.Min(inset.Left, Gap), inset.Top, inset.Right, inset.Bottom),
        PopupAnchorEdge.Top => new Thickness(inset.Left, Math.Min(inset.Top, Gap), inset.Right, inset.Bottom),
        PopupAnchorEdge.Right => new Thickness(inset.Left, inset.Top, Math.Min(inset.Right, Gap), inset.Bottom),
        PopupAnchorEdge.Bottom => new Thickness(inset.Left, inset.Top, inset.Right, Math.Min(inset.Bottom, Gap)),
        _ => inset,
    };

    /// <summary>The panel's corner, one gap from the anchor on the facing edge.</summary>
    /// <param name="anchor">The anchor rectangle.</param>
    /// <param name="facing">The panel edge that faces it.</param>
    /// <param name="width">The panel's width in physical pixels.</param>
    /// <param name="height">The panel's height in physical pixels.</param>
    /// <param name="gap">The gap in physical pixels.</param>
    /// <returns>The panel's top-left corner.</returns>
    private static PixelPoint Beside(PixelRect anchor, PopupAnchorEdge facing, int width, int height, int gap)
    {
        int centreX = anchor.X + (anchor.Width / 2);
        int centreY = anchor.Y + (anchor.Height / 2);

        return facing switch
        {
            PopupAnchorEdge.Left => new PixelPoint(anchor.Right + gap, centreY - (height / 2)),
            PopupAnchorEdge.Right => new PixelPoint(anchor.X - gap - width, centreY - (height / 2)),
            PopupAnchorEdge.Top => new PixelPoint(centreX - (width / 2), anchor.Bottom + gap),
            _ => new PixelPoint(centreX - (width / 2), anchor.Y - gap - height),
        };
    }

    private static PixelPoint Corner(PixelRect workingArea, int width, int height, int edge) =>
        new(workingArea.Right - edge - width, workingArea.Bottom - edge - height);

    /// <summary>
    /// Pushes the window off the anchor along the anchored axis when the trimmed inset was
    /// not enough on its own, as far as it can without taking the panel's leading edge off
    /// the working area.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is not enough when the clamp above moved the panel, and the clamp only moves it
    /// when the working area cannot hold the panel with its margins: the panel's near edge
    /// then sits closer to the icon than the gap.
    /// </para>
    /// <para>
    /// The push is bounded, and the bound is the working area's leading edge. It shipped
    /// unbounded, on the reasoning that the clamp was what caused the overlap so re-applying
    /// it would only restore it. That is true of the clamp and false of the panel: the clamp
    /// gives up the panel's <em>bottom</em>, and an unbounded push then moved the whole
    /// window by the whole overlap and took the panel's <em>top</em> with it. Measured on a
    /// 1920x1080 display at 150% with three providers, the panel opened 99 DIPs above the
    /// working area, which is the wordmark, the settings gear and the head of the dial gone
    /// with it — the second rule silently undoing the first. A tray icon that ignores half
    /// its clicks is a bad defect; a panel with no header, whose top can be neither seen nor
    /// scrolled to, is a worse one. So on a panel too tall to place at all, the icon is what
    /// gets covered, and <see cref="MaxWindowHeight"/> is what keeps that panel from
    /// arriving.
    /// </para>
    /// </remarks>
    /// <param name="window">The window rectangle.</param>
    /// <param name="anchor">The anchor rectangle.</param>
    /// <param name="facing">The panel edge that faces the anchor.</param>
    /// <param name="workingArea">The working area the panel's leading edge must stay inside.</param>
    /// <param name="left">The inset between the window's left edge and the panel's, in pixels.</param>
    /// <param name="top">The inset between the window's top edge and the panel's, in pixels.</param>
    /// <returns>The window rectangle, clear of the anchor as far as it can be.</returns>
    private static PixelRect Clear(
        PixelRect window,
        PixelRect anchor,
        PopupAnchorEdge facing,
        PixelRect workingArea,
        int left,
        int top)
    {
        if (facing is PopupAnchorEdge.None || !Overlaps(window, anchor))
        {
            return window;
        }

        (int dx, int dy) = facing switch
        {
            PopupAnchorEdge.Left => (anchor.Right - window.X, 0),
            PopupAnchorEdge.Right => (anchor.X - window.Right, 0),
            PopupAnchorEdge.Top => (0, anchor.Bottom - window.Y),
            _ => (0, anchor.Y - window.Bottom),
        };

        // Pushes towards the working area's far edge are free: they give up the panel's
        // bottom or its right, which is what an over-tall panel is already giving up. A push
        // the other way stops at the leading edge, whatever is left of the overlap.
        dx = Math.Max(dx, workingArea.X - (window.X + left));
        dy = Math.Max(dy, workingArea.Y - (window.Y + top));

        return new PixelRect(window.X + dx, window.Y + dy, window.Width, window.Height);
    }

    /// <summary>
    /// Whether two rectangles share a pixel. Both edges are exclusive, so a window whose
    /// bottom is the icon's top touches it without covering any of it.
    /// </summary>
    /// <param name="first">One rectangle.</param>
    /// <param name="second">The other.</param>
    /// <returns>True when they share at least one pixel.</returns>
    private static bool Overlaps(PixelRect first, PixelRect second) =>
        first.X < second.Right
        && second.X < first.Right
        && first.Y < second.Bottom
        && second.Y < first.Bottom;

    private static int Scale(double value, double scaling) => (int)Math.Round(value * scaling, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Clamps, preferring the lower bound when the panel is larger than the space. A panel
    /// taller than the working area loses its bottom rather than its top, because the top is
    /// the header and the head of the dial and the bottom is the last thing the panel says.
    /// <see cref="Clear"/> is bounded so that it cannot take back what this decides.
    /// </summary>
    private static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}

/// <summary>
/// The panel edge that faces the anchor, which is the side the taskbar is on.
/// </summary>
public enum PopupAnchorEdge
{
    /// <summary>There is no anchor, so no edge faces one and the panel takes a corner.</summary>
    None,

    /// <summary>The panel opens to the right of the anchor: a left-hand taskbar.</summary>
    Left,

    /// <summary>The panel opens below the anchor: a taskbar along the top.</summary>
    Top,

    /// <summary>The panel opens to the left of the anchor: a right-hand taskbar.</summary>
    Right,

    /// <summary>The panel opens above the anchor: the usual taskbar along the bottom.</summary>
    Bottom,
}

/// <summary>
/// Where the popup window goes, and how much transparent room it reserves for its shadow
/// once the edge facing the tray icon has been trimmed back off it.
/// </summary>
/// <param name="Window">The window rectangle in physical pixels.</param>
/// <param name="ShadowInset">
/// The inset the rectangle was built from, in device-independent units. The caller applies
/// it to the window's chrome; a window still carrying the untrimmed inset would put its
/// panel somewhere this arithmetic did not put it.
/// </param>
public readonly record struct PopupPlacementResult(PixelRect Window, Thickness ShadowInset)
{
    /// <summary>The window's top-left corner, which is what <c>Window.Position</c> takes.</summary>
    public PixelPoint Position => new(Window.X, Window.Y);
}
