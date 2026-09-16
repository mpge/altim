using Altim.Core.Models;

namespace Altim.Platform.MacOS;

/// <summary>
/// Turns a Cocoa screen rectangle into the top-left-origin
/// <see cref="PixelRect"/> the popup placement arithmetic expects.
/// </summary>
/// <remarks>
/// <para>
/// This is the one piece of the macOS layer that can be tested anywhere, so it is a pure
/// function over numbers rather than a method on the tray host.
/// </para>
/// <para>
/// <b>The flip.</b> Cocoa's global screen space has its origin at the <em>bottom</em> left
/// of the primary display — the one listed first in <c>NSScreen.screens</c>, which is the
/// display carrying the menu bar in the Displays arrangement — and its Y axis points up.
/// Altim, Avalonia and Core Graphics all use a top-left origin with Y pointing down.
/// Converting is therefore
/// <c>topLeftY = primaryScreenHeight - (cocoaY + height)</c>: subtracting the rectangle's
/// <em>top</em> edge, not its origin, because in Cocoa the origin is the bottom edge.
/// Getting that wrong puts the popup one status-item height away from the menu bar, which
/// is small enough to look like a rounding bug and is exactly why it is isolated here.
/// </para>
/// <para>
/// A display arranged above the primary one produces a negative Y, which is correct: the
/// virtual desktop extends above the origin in a top-left space.
/// </para>
/// <para>
/// <b>Unverified: the pixel scaling.</b> <see cref="PixelRect"/> is documented as physical
/// pixels and Avalonia documents <c>Screen.Bounds</c> as "raw pixel counts", so the points
/// Cocoa reports are multiplied by the window's <c>backingScaleFactor</c>. That has not
/// been checked against a Retina Mac. If the popup lands at half or double the right
/// position on a 2x display, the multiplication is the thing that is wrong, and the fix is
/// to pass <c>scale: 1</c> from
/// <see cref="MacOSTrayHost.GetAnchor"/> rather than to change the flip.
/// </para>
/// </remarks>
public static class MacOSCoordinates
{
    /// <summary>
    /// Converts a bottom-left-origin rectangle in points to a top-left-origin rectangle in
    /// physical pixels.
    /// </summary>
    /// <param name="x">Left edge in points, in Cocoa's global space.</param>
    /// <param name="y">
    /// <em>Bottom</em> edge in points, measured upwards from the bottom of the primary
    /// display.
    /// </param>
    /// <param name="width">Width in points.</param>
    /// <param name="height">Height in points.</param>
    /// <param name="primaryScreenHeight">
    /// Height in points of the first display in <c>NSScreen.screens</c>.
    /// </param>
    /// <param name="scale">
    /// The backing scale factor of the display the rectangle is on: 1 for a standard
    /// display, 2 for Retina. Values of zero or less are treated as 1.
    /// </param>
    /// <returns>
    /// The rectangle in physical pixels, or <see langword="null"/> when any input is not a
    /// finite number or the rectangle encloses no area. A null sends the caller to the next
    /// positioning tier, which is what <see cref="Core.Abstractions.IPlatformService"/>
    /// requires; it is never an empty rectangle at the origin.
    /// </returns>
    public static PixelRect? ToPixelRect(
        double x, double y, double width, double height, double primaryScreenHeight, double scale)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || !double.IsFinite(primaryScreenHeight) || !double.IsFinite(scale))
        {
            return null;
        }

        if (width <= 0 || height <= 0 || primaryScreenHeight <= 0)
        {
            return null;
        }

        double factor = scale > 0 ? scale : 1d;
        double topLeftY = primaryScreenHeight - (y + height);

        int pixelWidth = Math.Max(1, Round(width * factor));
        int pixelHeight = Math.Max(1, Round(height * factor));

        return new PixelRect(Round(x * factor), Round(topLeftY * factor), pixelWidth, pixelHeight);
    }

    private static int Round(double value) =>
        (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
}
