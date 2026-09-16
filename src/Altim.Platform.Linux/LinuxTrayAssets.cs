using System.Globalization;
using Altim.Core.Models;

namespace Altim.Platform.Linux;

/// <summary>
/// Picks the StatusNotifierItem icon asset.
/// </summary>
/// <remarks>
/// <para>
/// <b>The panel's background colour is not knowable.</b> StatusNotifierItem hands the host a
/// bitmap and says nothing about what it will be drawn on; there is no equivalent of the
/// macOS template image and no equivalent of the Windows taskbar's light-or-dark registry
/// value. The best available signal is the desktop's own colour-scheme preference from the
/// appearance portal, on the reasoning that a user running a dark desktop is running a dark
/// panel, so <see cref="TrayIconVariant.Automatic"/> follows it:
/// the light glyph on a dark desktop, the dark glyph otherwise. A machine with no portal has
/// no preference, which resolves to light — and therefore to the dark glyph.
/// </para>
/// <para>
/// That is a guess, and it is stated as one. A user on a dark panel inside a light desktop
/// theme gets a dark glyph on a dark panel, and the fix for them is the explicit
/// <see cref="TrayIconVariant.Light"/> value rather than a cleverer guess here.
/// </para>
/// </remarks>
public static class LinuxTrayAssets
{
    /// <summary>
    /// The size asked for, in pixels. StatusNotifierItem hosts scale whatever they are given;
    /// 32 survives a 2x panel without being large enough to matter.
    /// </summary>
    public const int PreferredPixelSize = 32;

    /// <summary>Sizes present in <c>assets/icons</c> for the dark glyph.</summary>
    private static readonly int[] DarkSizes = [16, 20, 24, 32, 48, 64, 128, 256, 512];

    /// <summary>Sizes present in <c>assets/icons</c> for the light glyph.</summary>
    private static readonly int[] LightSizes = [16, 20, 24, 32, 44, 64, 512];

    /// <summary>
    /// Resolves a variant to a glyph.
    /// </summary>
    /// <param name="variant">The requested rendering.</param>
    /// <param name="desktopIsDark">
    /// The appearance portal's current answer, or false when it has not answered or there is
    /// no portal.
    /// </param>
    /// <returns>True for the light glyph, which is the one with contrast on a dark panel.</returns>
    public static bool UsesLightGlyph(TrayIconVariant variant, bool desktopIsDark) => variant switch
    {
        TrayIconVariant.Light => true,
        TrayIconVariant.Dark => false,
        _ => desktopIsDark,
    };

    /// <summary>
    /// Chooses the asset drawn closest to, and not smaller than, a wanted pixel size.
    /// </summary>
    /// <param name="light">True for the light glyph.</param>
    /// <param name="wantedPixels">The wanted size in pixels.</param>
    /// <returns>One of the sizes actually present for that glyph.</returns>
    public static int ChooseAssetSize(bool light, int wantedPixels)
    {
        int[] sizes = light ? LightSizes : DarkSizes;

        foreach (int candidate in sizes)
        {
            if (candidate >= wantedPixels)
            {
                return candidate;
            }
        }

        return sizes[^1];
    }

    /// <summary>
    /// The file name of one rendering.
    /// </summary>
    /// <param name="light">True for the light glyph.</param>
    /// <param name="size">A size from <see cref="ChooseAssetSize"/>.</param>
    /// <returns>The file name, with no directory component.</returns>
    public static string FileName(bool light, int size) =>
        (light ? "altim-white-" : "altim-") + size.ToString(CultureInfo.InvariantCulture) + ".png";

    /// <summary>
    /// Resolves an asset file inside a directory.
    /// </summary>
    /// <param name="directory">The directory holding the icon assets.</param>
    /// <param name="light">True for the light glyph.</param>
    /// <param name="wantedPixels">The wanted size in pixels.</param>
    /// <returns>
    /// A full path, or <see langword="null"/> when the directory holds no rendering of that
    /// glyph at all. The preferred size is tried first and then every other size, so a
    /// partial asset set still produces an icon rather than an empty panel slot.
    /// </returns>
    public static string? Resolve(string directory, bool light, int wantedPixels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string preferred = Path.Combine(directory, FileName(light, ChooseAssetSize(light, wantedPixels)));
        if (File.Exists(preferred))
        {
            return preferred;
        }

        foreach (int candidate in light ? LightSizes : DarkSizes)
        {
            string path = Path.Combine(directory, FileName(light, candidate));
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the directory holding the icon assets, the same walk the Windows loader does.
    /// </summary>
    /// <param name="baseDirectory">
    /// Where to start, normally <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <returns>The directory, or <see langword="null"/> when no asset was found.</returns>
    public static string? DiscoverAssetDirectory(string? baseDirectory)
    {
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(baseDirectory);
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            string candidate = Path.Combine(current.FullName, "assets", "icons");
            if (File.Exists(Path.Combine(candidate, FileName(light: false, 16))))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}
