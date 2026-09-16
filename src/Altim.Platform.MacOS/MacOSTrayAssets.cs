using System.Globalization;

namespace Altim.Platform.MacOS;

/// <summary>
/// Picks the menu bar icon asset. The macOS tray icon is a <em>template image</em>, so
/// there is exactly one set of renderings and no light or dark variant.
/// </summary>
/// <remarks>
/// <para>
/// A template image is a black glyph plus an alpha channel; the menu bar draws it in
/// whatever colour has contrast against the wallpaper behind it, and re-tints it when the
/// wallpaper or the appearance changes. That is why
/// <see cref="Core.Abstractions.ITrayHost.SetIconAsync"/> accepts every
/// <see cref="Core.Models.TrayIconVariant"/> on macOS and ignores it: swapping to a "light"
/// rendering would produce a white glyph that the menu bar then tints again, and it would
/// be wrong on exactly the displays it was meant to fix.
/// </para>
/// <para>
/// Sizing is in points, not pixels. The menu bar is 22 points tall and Apple's guidance
/// leaves a little room around the glyph, so the image is sized to
/// <see cref="MenuBarPointSize"/> points and the <em>asset</em> is chosen at the pixel size
/// that lands on for the display's backing scale factor — 18 pixels at 1x, 36 at 2x.
/// </para>
/// <para>
/// The selection below is pure and covered by tests. Whether 18 points is the right size
/// for Altim's glyph specifically is a visual judgement that has not been made on a Mac.
/// </para>
/// </remarks>
public static class MacOSTrayAssets
{
    /// <summary>The square size, in points, the menu bar image is set to.</summary>
    public const int MenuBarPointSize = 18;

    /// <summary>The file name prefix of the template renderings in <c>assets/icons</c>.</summary>
    public const string FileNamePrefix = "altim-template-";

    /// <summary>The pixel sizes <c>assets/icons</c> actually contains for the template glyph.</summary>
    private static readonly int[] AvailableSizes = [16, 20, 24, 32, 44, 64, 512];

    /// <summary>
    /// The pixel sizes available, smallest first.
    /// </summary>
    public static IReadOnlyList<int> Sizes => AvailableSizes;

    /// <summary>
    /// The pixel size the image should be rendered at for a given backing scale factor.
    /// </summary>
    /// <param name="scale">The display's backing scale factor; 1 or 2 in practice.</param>
    /// <returns>
    /// <see cref="MenuBarPointSize"/> multiplied by the scale, rounded, and never below
    /// <see cref="MenuBarPointSize"/>. A nonsensical scale falls back to 1x rather than
    /// producing a zero-sized image.
    /// </returns>
    public static int PixelSizeForScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return MenuBarPointSize;
        }

        int pixels = (int)Math.Round(MenuBarPointSize * scale, MidpointRounding.AwayFromZero);
        return pixels < MenuBarPointSize ? MenuBarPointSize : pixels;
    }

    /// <summary>
    /// Chooses the asset drawn closest to, and not smaller than, a wanted pixel size.
    /// </summary>
    /// <param name="wantedPixels">The size the menu bar will draw at, in physical pixels.</param>
    /// <returns>
    /// One of <see cref="Sizes"/>. Downscaling a larger rendering keeps the glyph's
    /// proportions; upscaling a smaller one throws away the hinting it was drawn with, so
    /// the search always rounds up and only falls back to the largest asset when nothing is
    /// big enough.
    /// </returns>
    public static int ChooseAssetSize(int wantedPixels)
    {
        foreach (int candidate in AvailableSizes)
        {
            if (candidate >= wantedPixels)
            {
                return candidate;
            }
        }

        return AvailableSizes[^1];
    }

    /// <summary>
    /// The file name of one rendering.
    /// </summary>
    /// <param name="size">A size from <see cref="Sizes"/>.</param>
    /// <returns>The file name, with no directory component.</returns>
    public static string FileName(int size) =>
        FileNamePrefix + size.ToString(CultureInfo.InvariantCulture) + ".png";

    /// <summary>
    /// Resolves an asset file for a wanted pixel size inside a directory.
    /// </summary>
    /// <param name="directory">The directory holding <c>altim-template-*.png</c>.</param>
    /// <param name="wantedPixels">The size the menu bar will draw at, in physical pixels.</param>
    /// <returns>
    /// A full path, or <see langword="null"/> when the directory holds no template
    /// rendering at all. The preferred size is tried first and then every other size, so a
    /// partial asset set still produces an icon rather than an invisible menu bar item.
    /// </returns>
    public static string? Resolve(string directory, int wantedPixels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string preferred = Path.Combine(directory, FileName(ChooseAssetSize(wantedPixels)));
        if (File.Exists(preferred))
        {
            return preferred;
        }

        foreach (int candidate in AvailableSizes)
        {
            string path = Path.Combine(directory, FileName(candidate));
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the directory holding the template renderings.
    /// </summary>
    /// <param name="executablePath">
    /// The running executable, normally <see cref="Environment.ProcessPath"/>. Used to find
    /// the bundle's <c>Contents/Resources</c> directory when there is one.
    /// </param>
    /// <param name="baseDirectory">
    /// Where to start walking when the executable is not bundled, normally
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <returns>
    /// The directory, or <see langword="null"/> when no template asset was found. A null
    /// leaves the status item without an image, which the host reports rather than throws.
    /// </returns>
    public static string? DiscoverAssetDirectory(string? executablePath, string? baseDirectory)
    {
        string? resources = MacOSAppBundle.FindResourcesDirectory(executablePath);
        if (resources is not null && ContainsTemplateAsset(resources))
        {
            return resources;
        }

        if (resources is not null)
        {
            string nested = Path.Combine(resources, "assets", "icons");
            if (ContainsTemplateAsset(nested))
            {
                return nested;
            }
        }

        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        // Same walk the Windows loader does, so a build output inside the repository finds
        // the assets without a copy step.
        var current = new DirectoryInfo(baseDirectory);
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            string candidate = Path.Combine(current.FullName, "assets", "icons");
            if (ContainsTemplateAsset(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool ContainsTemplateAsset(string directory)
    {
        foreach (int candidate in AvailableSizes)
        {
            if (File.Exists(Path.Combine(directory, FileName(candidate))))
            {
                return true;
            }
        }

        return false;
    }
}
