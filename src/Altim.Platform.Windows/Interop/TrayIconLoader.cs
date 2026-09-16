#if WINDOWS

using System.Globalization;
using Altim.Core.Models;

namespace Altim.Platform.Windows.Interop;

/// <summary>
/// Turns the repository icon assets into an <c>HICON</c> at the size the current
/// notification area actually wants.
/// </summary>
/// <remarks>
/// <para>
/// The assets are PNGs at fixed sizes, which is the right shape for a tray icon:
/// scaling a 512 pixel glyph down to 16 pixels in software loses the hinting the
/// smaller renderings were drawn with. So the loader asks Windows how big a small
/// icon is at the notification area's DPI — 16 at 100%, 20 at 125%, 24 at 150%, 32 at
/// 200% — and picks the asset drawn at that size, falling back to the next size up
/// and only then to a larger one scaled down.
/// </para>
/// <para>
/// Decoding is done by GDI+, which is part of Windows, rather than by a managed image
/// library: <c>System.Drawing.Common</c> is not referenced, and a PNG decoder written
/// by hand would be a liability. <c>altim.ico</c> is the fallback, loaded through
/// <c>LoadImage</c> so the shell picks the closest frame itself, and the stock
/// application icon is the last resort so a missing asset degrades to a visible icon
/// rather than to no tray presence at all.
/// </para>
/// </remarks>
internal sealed unsafe class TrayIconLoader : IDisposable
{
    /// <summary>Sizes present in <c>assets/icons</c> for the dark glyph.</summary>
    private static readonly int[] DarkSizes = [16, 20, 24, 32, 48, 64, 128, 256, 512];

    /// <summary>Sizes present in <c>assets/icons</c> for the light glyph.</summary>
    private static readonly int[] LightSizes = [16, 20, 24, 32, 44, 64, 512];

    private readonly string? _iconDirectory;
    private readonly Lock _gate = new();

    private nuint _gdiPlusToken;
    private bool _gdiPlusStarted;
    private bool _disposed;

    /// <summary>
    /// Creates a loader over an icon directory.
    /// </summary>
    /// <param name="iconDirectory">
    /// The directory holding <c>altim-*.png</c> and <c>altim.ico</c>, or
    /// <see langword="null"/> to discover it beside the application.
    /// </param>
    public TrayIconLoader(string? iconDirectory = null) =>
        _iconDirectory = iconDirectory ?? DiscoverIconDirectory();

    /// <summary>The directory the assets were found in, or null when none was found.</summary>
    public string? IconDirectory => _iconDirectory;

    /// <summary>
    /// The width of a notification area icon at <paramref name="dpi"/>, in physical
    /// pixels.
    /// </summary>
    /// <param name="dpi">Dots per inch of the display showing the taskbar.</param>
    /// <returns>The size Windows expects, never less than 16.</returns>
    public static int MetricSizeForDpi(uint dpi)
    {
        int size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        return size < 16 ? 16 : size;
    }

    /// <summary>
    /// Reports the DPI the notification area is rendered at.
    /// </summary>
    /// <returns>
    /// The DPI of the display showing the taskbar, or the system DPI when the taskbar
    /// cannot be found — which happens while Explorer is restarting.
    /// </returns>
    public static uint CurrentTrayDpi()
    {
        IntPtr taskbar;
        fixed (char* className = "Shell_TrayWnd")
        {
            taskbar = NativeMethods.FindWindowW(className, null);
        }

        if (taskbar != IntPtr.Zero)
        {
            uint dpi = NativeMethods.GetDpiForWindow(taskbar);
            if (dpi != 0)
            {
                return dpi;
            }
        }

        uint system = NativeMethods.GetDpiForSystem();
        return system == 0 ? 96 : system;
    }

    /// <summary>
    /// Resolves a variant to the glyph that will have contrast against the current
    /// notification area.
    /// </summary>
    /// <param name="variant">The requested rendering.</param>
    /// <returns>
    /// True for the light glyph. <see cref="TrayIconVariant.Automatic"/> follows the
    /// taskbar's own light or dark setting, which is not the same setting as the
    /// application theme.
    /// </returns>
    public static bool UsesLightGlyph(TrayIconVariant variant) => variant switch
    {
        TrayIconVariant.Light => true,
        TrayIconVariant.Dark => false,
        _ => WindowsTheme.TaskbarIsDark(),
    };

    /// <summary>
    /// Loads one rendering of the icon at a given size.
    /// </summary>
    /// <param name="light">True for the light glyph, from <see cref="UsesLightGlyph"/>.</param>
    /// <param name="size">The wanted width and height in physical pixels.</param>
    /// <param name="shared">
    /// True when the returned handle is a shared system icon, which must not be passed
    /// to <c>DestroyIcon</c>.
    /// </param>
    /// <returns>
    /// An icon handle the caller owns and must destroy unless <paramref name="shared"/>
    /// is set, or <see cref="IntPtr.Zero"/> when even the stock icon could not be
    /// loaded.
    /// </returns>
    public IntPtr Load(bool light, int size, out bool shared)
    {
        shared = false;

        if (_iconDirectory is not null)
        {
            string? png = ResolveAsset(_iconDirectory, light, size);
            if (png is not null)
            {
                IntPtr fromPng = LoadPng(png);
                if (fromPng != IntPtr.Zero)
                {
                    return fromPng;
                }
            }

            string ico = Path.Combine(_iconDirectory, "altim.ico");
            if (File.Exists(ico))
            {
                IntPtr fromIco;
                fixed (char* path = ico)
                {
                    fromIco = NativeMethods.LoadImageW(
                        IntPtr.Zero, path, NativeMethods.IMAGE_ICON, size, size,
                        NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTCOLOR);
                }

                if (fromIco != IntPtr.Zero)
                {
                    return fromIco;
                }
            }
        }

        // Last resort so a missing asset still leaves a tray presence. This handle
        // belongs to the system and is never destroyed.
        shared = true;
        return NativeMethods.LoadIconW(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
    }

    /// <summary>Shuts GDI+ down if this loader started it.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_gdiPlusStarted)
            {
                NativeMethods.GdiplusShutdown(_gdiPlusToken);
                _gdiPlusStarted = false;
            }
        }
    }

    /// <summary>
    /// Finds the icon assets: beside the application first, then by walking up the
    /// directory tree, which is what makes a build output inside the repository work
    /// without a copy step.
    /// </summary>
    /// <returns>The directory, or null when the assets are not present.</returns>
    private static string? DiscoverIconDirectory()
    {
        string? baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(baseDirectory);
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            string candidate = Path.Combine(current.FullName, "assets", "icons");
            if (File.Exists(Path.Combine(candidate, "altim-16.png")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Picks the asset drawn closest to, and not smaller than, the wanted size.
    /// </summary>
    /// <param name="directory">The icon directory.</param>
    /// <param name="light">True for the light glyph used on a dark taskbar.</param>
    /// <param name="size">The wanted size in physical pixels.</param>
    /// <returns>A file path, or null when no asset file exists.</returns>
    private static string? ResolveAsset(string directory, bool light, int size)
    {
        int[] sizes = light ? LightSizes : DarkSizes;
        string prefix = light ? "altim-white-" : "altim-";

        int chosen = sizes[^1];
        foreach (int candidate in sizes)
        {
            if (candidate >= size)
            {
                chosen = candidate;
                break;
            }
        }

        string path = Path.Combine(directory, $"{prefix}{chosen.ToString(CultureInfo.InvariantCulture)}.png");
        if (File.Exists(path))
        {
            return path;
        }

        // The exact size is missing: take any other rendering rather than none.
        foreach (int candidate in sizes)
        {
            string fallback = Path.Combine(directory, $"{prefix}{candidate.ToString(CultureInfo.InvariantCulture)}.png");
            if (File.Exists(fallback))
            {
                return fallback;
            }
        }

        return null;
    }

    private IntPtr LoadPng(string path)
    {
        if (!EnsureGdiPlus())
        {
            return IntPtr.Zero;
        }

        IntPtr bitmap;
        int status;
        fixed (char* file = path)
        {
            status = NativeMethods.GdipCreateBitmapFromFile(file, &bitmap);
        }

        if (status != 0 || bitmap == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr icon;
        status = NativeMethods.GdipCreateHICONFromBitmap(bitmap, &icon);
        _ = NativeMethods.GdipDisposeImage(bitmap);

        return status == 0 ? icon : IntPtr.Zero;
    }

    private bool EnsureGdiPlus()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_gdiPlusStarted)
            {
                return true;
            }

            var input = new GdiplusStartupInput
            {
                GdiplusVersion = 1,
                SuppressBackgroundThread = 0,
                SuppressExternalCodecs = 0,
            };

            nuint token;
            int status = NativeMethods.GdiplusStartup(&token, &input, IntPtr.Zero);
            if (status != 0)
            {
                return false;
            }

            _gdiPlusToken = token;
            _gdiPlusStarted = true;
            return true;
        }
    }
}

#endif
