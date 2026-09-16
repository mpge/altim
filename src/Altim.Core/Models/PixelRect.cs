namespace Altim.Core.Models;

/// <summary>
/// A rectangle in physical screen pixels.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately mirrors Avalonia's <c>PixelRect</c>. <c>Altim.Core</c> depends on
/// the BCL and nothing else, so the geometry used by
/// <see cref="Abstractions.IPlatformService"/> and <see cref="Abstractions.ITrayHost"/>
/// is declared here and converted at the UI boundary. The two types are
/// field-for-field identical, so the conversion is a component-wise copy.
/// </para>
/// <para>
/// The units are physical pixels, while window sizes are device-independent units.
/// Any arithmetic mixing the two must scale by the factor of the screen the popup is
/// being placed on, not the primary screen.
/// </para>
/// </remarks>
/// <param name="X">Left edge, in physical pixels, in virtual screen coordinates.</param>
/// <param name="Y">Top edge, in physical pixels, in virtual screen coordinates.</param>
/// <param name="Width">Width in physical pixels.</param>
/// <param name="Height">Height in physical pixels.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>Right edge, exclusive, in physical pixels.</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge, exclusive, in physical pixels.</summary>
    public int Bottom => Y + Height;

    /// <summary>
    /// True when the rectangle encloses no area. An empty rectangle is not a usable
    /// anchor, so callers fall through to the next positioning tier rather than
    /// placing a window against it.
    /// </summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
