using Avalonia;
using CoreRect = Altim.Core.Models.PixelRect;

namespace Altim.App.Views;

/// <summary>
/// Where the popup window goes, as arithmetic with no Avalonia window in it.
/// </summary>
/// <remarks>
/// <para>
/// Three things make this harder than it looks, and all three are handled here.
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
/// what should line up with the tray icon, so the computed panel rectangle is converted back
/// to a window position by subtracting the shadow inset.
/// </para>
/// <para>
/// <b>Which edge.</b> The taskbar is not always at the bottom. The anchor's nearest
/// working-area edge is taken as the taskbar edge and the panel is placed on the opposite
/// side of the anchor along that axis, then clamped into the working area, so a panel next
/// to a left-hand taskbar opens to its right rather than off the screen.
/// </para>
/// </remarks>
internal static class PopupPlacement
{
    /// <summary>Distance between the tray icon and the panel, in device-independent units.</summary>
    public const double Gap = 8;

    /// <summary>Smallest distance between the panel and the working area's edge, in DIPs.</summary>
    public const double EdgeMargin = 8;

    /// <summary>
    /// Computes the window's top-left corner in physical pixels.
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
    /// <param name="windowSize">The window's size in device-independent units.</param>
    /// <param name="shadowInset">The transparent margin around the panel, in DIPs.</param>
    /// <returns>
    /// The position to give <c>Window.Position</c>, chosen so that the <em>panel</em>, not
    /// the window, sits beside the anchor and inside the working area.
    /// </returns>
    public static PixelPoint Compute(
        CoreRect? anchor,
        PixelPoint? cursor,
        PixelRect workingArea,
        double scaling,
        Size windowSize,
        Thickness shadowInset)
    {
        double scale = scaling > 0 ? scaling : 1d;

        int insetLeft = Scale(shadowInset.Left, scale);
        int insetTop = Scale(shadowInset.Top, scale);
        int insetRight = Scale(shadowInset.Right, scale);
        int insetBottom = Scale(shadowInset.Bottom, scale);

        int panelWidth = Math.Max(1, Scale(windowSize.Width, scale) - insetLeft - insetRight);
        int panelHeight = Math.Max(1, Scale(windowSize.Height, scale) - insetTop - insetBottom);

        int gap = Scale(Gap, scale);
        int edge = Scale(EdgeMargin, scale);

        PixelRect target = Resolve(anchor, cursor);
        PixelPoint panel = target.Width > 0 && target.Height > 0
            ? Beside(target, workingArea, panelWidth, panelHeight, gap)
            : Corner(workingArea, panelWidth, panelHeight, edge);

        int x = Clamp(panel.X, workingArea.X + edge, workingArea.Right - edge - panelWidth);
        int y = Clamp(panel.Y, workingArea.Y + edge, workingArea.Bottom - edge - panelHeight);

        return new PixelPoint(x - insetLeft, y - insetTop);
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

    private static PixelPoint Beside(PixelRect anchor, PixelRect workingArea, int width, int height, int gap)
    {
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
            return new PixelPoint(anchor.Right + gap, centreY - (height / 2));
        }

        if (nearest == fromRight)
        {
            return new PixelPoint(anchor.X - gap - width, centreY - (height / 2));
        }

        if (nearest == fromTop)
        {
            return new PixelPoint(centreX - (width / 2), anchor.Bottom + gap);
        }

        return new PixelPoint(centreX - (width / 2), anchor.Y - gap - height);
    }

    private static PixelPoint Corner(PixelRect workingArea, int width, int height, int edge) =>
        new(workingArea.Right - edge - width, workingArea.Bottom - edge - height);

    private static int Scale(double value, double scaling) => (int)Math.Round(value * scaling, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Clamps, preferring the lower bound when the panel is larger than the space. A panel
    /// taller than the working area is clipped at the bottom rather than at the top, because
    /// the top is where the provider names are.
    /// </summary>
    private static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}
