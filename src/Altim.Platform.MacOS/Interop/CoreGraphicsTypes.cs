using System.Runtime.InteropServices;

namespace Altim.Platform.MacOS.Interop;

/// <summary>
/// A Core Graphics point in screen coordinates, which on macOS are points with the
/// origin at the <em>bottom</em> left of the primary display.
/// </summary>
/// <remarks>
/// <c>CGFloat</c> is a <see cref="double"/> on every 64-bit Apple platform, and macOS has
/// been 64-bit only since 10.15, so the 32-bit float form is deliberately not handled.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    /// <summary>Distance from the left edge of the primary display, in points.</summary>
    public double X;

    /// <summary>Distance <em>up</em> from the bottom edge of the primary display, in points.</summary>
    public double Y;
}

/// <summary>A Core Graphics size in points.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGSize
{
    /// <summary>Width in points.</summary>
    public double Width;

    /// <summary>Height in points.</summary>
    public double Height;

    /// <summary>Creates a size.</summary>
    /// <param name="width">Width in points.</param>
    /// <param name="height">Height in points.</param>
    public CGSize(double width, double height)
    {
        Width = width;
        Height = height;
    }
}

/// <summary>
/// A Core Graphics rectangle in points, bottom-left origin.
/// </summary>
/// <remarks>
/// Converting one of these to Altim's top-left-origin
/// <see cref="Core.Models.PixelRect"/> is <see cref="MacOSCoordinates"/>' job, and is
/// kept out of this struct so the flip can be tested without a Mac.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    /// <summary>The bottom-left corner.</summary>
    public CGPoint Origin;

    /// <summary>The extent.</summary>
    public CGSize Size;
}
