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

        return new PopupPlacementResult(Clear(window, target, facing), inset);
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
    /// not enough on its own.
    /// </summary>
    /// <remarks>
    /// It is not enough when the clamp above moved the panel: a working area that cannot
    /// hold the panel with its margins puts the panel's near edge closer to the icon than
    /// the gap. The push is not clamped back into the working area, because the clamp is
    /// what caused the overlap and re-applying it would only restore it. A panel a few
    /// pixels outside its margin is visible; a tray icon that ignores half its clicks is
    /// not, which is what makes it the worse of the two.
    /// </remarks>
    /// <param name="window">The window rectangle.</param>
    /// <param name="anchor">The anchor rectangle.</param>
    /// <param name="facing">The panel edge that faces the anchor.</param>
    /// <returns>The window rectangle, clear of the anchor.</returns>
    private static PixelRect Clear(PixelRect window, PixelRect anchor, PopupAnchorEdge facing)
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
    /// taller than the working area is clipped at the bottom rather than at the top, because
    /// the top is where the provider names are.
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
