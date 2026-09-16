using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// One captured frame, copied out of the platform bitmap into managed memory so the bitmap
/// can be disposed straight away: an undisposed frame takes the renderer down on Linux.
/// </summary>
/// <remarks>
/// Everything the design system promises is a promise about pixels. A frame size or a
/// property value can be right while the screen is wrong - a clipped ring, a clipped
/// shadow, a panel inset out of its own width - so the assertions here read the colours
/// the renderer actually produced.
/// </remarks>
internal sealed class Frame
{
    private readonly byte[] _pixels;
    private readonly int _stride;
    private readonly bool _blueFirst;

    private Frame(byte[] pixels, int stride, bool blueFirst, PixelSize size)
    {
        _pixels = pixels;
        _stride = stride;
        _blueFirst = blueFirst;
        Width = size.Width;
        Height = size.Height;
    }

    /// <summary>The frame width in device pixels.</summary>
    public int Width { get; }

    /// <summary>The frame height in device pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Ticks the render timer, captures the last frame and copies it out. The platform
    /// bitmap is disposed before this returns.
    /// </summary>
    /// <param name="topLevel">The window to capture.</param>
    /// <returns>The captured pixels.</returns>
    public static Frame Capture(TopLevel topLevel)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

        using WriteableBitmap? bitmap = topLevel.CaptureRenderedFrame();
        Assert.NotNull(bitmap);

        using ILockedFramebuffer buffer = bitmap.Lock();
        bool blueFirst = buffer.Format == PixelFormats.Bgra8888;
        Assert.True(
            blueFirst || buffer.Format == PixelFormats.Rgba8888,
            $"Unexpected capture format {buffer.Format}.");

        var pixels = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        return new Frame(pixels, buffer.RowBytes, blueFirst, buffer.Size);
    }

    /// <summary>Reads one pixel.</summary>
    /// <param name="x">The device pixel column.</param>
    /// <param name="y">The device pixel row.</param>
    /// <returns>The colour, alpha included.</returns>
    public Color At(int x, int y)
    {
        Assert.InRange(x, 0, Width - 1);
        Assert.InRange(y, 0, Height - 1);

        int offset = (y * _stride) + (x * 4);
        byte first = _pixels[offset];
        byte green = _pixels[offset + 1];
        byte third = _pixels[offset + 2];
        byte alpha = _pixels[offset + 3];

        return _blueFirst
            ? Color.FromArgb(alpha, third, green, first)
            : Color.FromArgb(alpha, first, green, third);
    }

    /// <summary>Reads one pixel at a layout position.</summary>
    /// <param name="point">The position in device independent pixels at scale 1.</param>
    /// <returns>The colour, alpha included.</returns>
    public Color At(Point point) => At((int)Math.Round(point.X), (int)Math.Round(point.Y));

    /// <summary>Counts the pixels inside an area that match.</summary>
    /// <param name="area">The area, clamped to the frame.</param>
    /// <param name="match">The predicate.</param>
    /// <returns>The number of matching pixels.</returns>
    public int Count(Rect area, Func<Color, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        int count = 0;
        foreach ((int x, int y) in Positions(area))
        {
            if (match(At(x, y)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The longest unbroken run of matching pixels along one row.</summary>
    /// <param name="y">The device pixel row.</param>
    /// <param name="match">The predicate.</param>
    /// <returns>The length of the longest run, in device pixels.</returns>
    public int LongestRun(int y, Func<Color, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        int longest = 0;
        int run = 0;
        for (int x = 0; x < Width; x++)
        {
            run = match(At(x, y)) ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    /// <summary>
    /// The width from the first matching pixel on a row to the last, inclusive. Unlike
    /// <see cref="LongestRun"/> this tolerates the antialiased edge of a shape, which is
    /// what the outer column of a rounded rectangle always is.
    /// </summary>
    /// <param name="y">The device pixel row.</param>
    /// <param name="match">The predicate.</param>
    /// <returns>The span in device pixels, or zero when nothing matches.</returns>
    public int SpanAt(int y, Func<Color, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        int first = -1;
        int last = -1;
        for (int x = 0; x < Width; x++)
        {
            if (!match(At(x, y)))
            {
                continue;
            }

            first = first < 0 ? x : first;
            last = x;
        }

        return first < 0 ? 0 : last - first + 1;
    }

    /// <summary>The mean alpha inside an area, from 0 to 255.</summary>
    /// <param name="area">The area, clamped to the frame.</param>
    /// <returns>The mean, or zero when the area is empty.</returns>
    public double MeanAlpha(Rect area)
    {
        double total = 0d;
        int count = 0;
        foreach ((int x, int y) in Positions(area))
        {
            total += At(x, y).A;
            count++;
        }

        return count == 0 ? 0d : total / count;
    }

    /// <summary>Whether any pixel inside an area matches.</summary>
    /// <param name="area">The area, clamped to the frame.</param>
    /// <param name="match">The predicate.</param>
    /// <returns>True when at least one pixel matches.</returns>
    public bool Any(Rect area, Func<Color, bool> match) => Count(area, match) > 0;

    /// <summary>The distinct colours inside an area, most common first.</summary>
    /// <param name="area">The area, clamped to the frame.</param>
    /// <param name="take">How many colours to report.</param>
    /// <returns>A readable summary, for failure messages.</returns>
    public string Describe(Rect area, int take = 4)
    {
        Dictionary<Color, int> seen = [];
        foreach ((int x, int y) in Positions(area))
        {
            Color colour = At(x, y);
            seen[colour] = seen.TryGetValue(colour, out int count) ? count + 1 : 1;
        }

        IEnumerable<string> top = seen
            .OrderByDescending(pair => pair.Value)
            .Take(take)
            .Select(pair => $"{pair.Key}x{pair.Value}");

        return string.Join(", ", top);
    }

    /// <summary>Whether two frames differ anywhere inside an area.</summary>
    /// <param name="other">The frame to compare with.</param>
    /// <param name="area">The area, clamped to both frames.</param>
    /// <returns>The number of differing pixels.</returns>
    public int DifferenceWith(Frame other, Rect area)
    {
        ArgumentNullException.ThrowIfNull(other);

        int different = 0;
        foreach ((int x, int y) in Positions(area))
        {
            if (x >= other.Width || y >= other.Height)
            {
                continue;
            }

            if (At(x, y) != other.At(x, y))
            {
                different++;
            }
        }

        return different;
    }

    private IEnumerable<(int X, int Y)> Positions(Rect area)
    {
        int left = Math.Max(0, (int)Math.Floor(area.X));
        int top = Math.Max(0, (int)Math.Floor(area.Y));
        int right = Math.Min(Width, (int)Math.Ceiling(area.Right));
        int bottom = Math.Min(Height, (int)Math.Ceiling(area.Bottom));

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                yield return (x, y);
            }
        }
    }
}

/// <summary>
/// A window held open across several captures, so a test can render, change something and
/// render again. <see cref="DesignSystem.Render"/> closes its window before returning,
/// which is right for a one shot assertion and useless for a before and after.
/// </summary>
internal sealed class PixelHost : IDisposable
{
    private PixelHost(Window window) => Window = window;

    /// <summary>The hosting window.</summary>
    public Window Window { get; }

    /// <summary>Shows a control in a default Altim window.</summary>
    /// <param name="content">The control to host.</param>
    /// <param name="variant">The theme variant to render under.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    /// <returns>The open host.</returns>
    public static PixelHost Show(
        Control content,
        ThemeVariant? variant = null,
        double width = 320d,
        double height = 160d)
    {
        _ = DesignSystem.Ensure();

        return Show(new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            Width = width,
            Height = height,
            RequestedThemeVariant = variant ?? ThemeVariant.Light,
            Content = content,
        });
    }

    /// <summary>Shows a window the caller built.</summary>
    /// <param name="window">The window to show.</param>
    /// <returns>The open host.</returns>
    public static PixelHost Show(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _ = DesignSystem.Ensure();

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new PixelHost(window);
    }

    /// <summary>Resolves a control theme from the merged design system.</summary>
    /// <param name="key">The resource key.</param>
    /// <returns>The theme.</returns>
    public static ControlTheme Theme(string key)
    {
        _ = DesignSystem.Ensure();
        Application app = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(
            app.Resources.TryGetResource(key, ThemeVariant.Light, out object? theme),
            $"{key} does not resolve.");
        return Assert.IsType<ControlTheme>(theme);
    }

    /// <summary>Finds a template part by name.</summary>
    /// <typeparam name="T">The part type.</typeparam>
    /// <param name="root">The control whose template to search.</param>
    /// <param name="name">The part name.</param>
    /// <returns>The part.</returns>
    public static T Part<T>(Visual root, string name)
        where T : Visual
    {
        ArgumentNullException.ThrowIfNull(root);

        T? part = root.GetVisualDescendants().OfType<T>().FirstOrDefault(v => v.Name == name);
        Assert.True(part is not null, $"{name} is missing from the template.");
        return part!;
    }

    /// <summary>Captures the current frame.</summary>
    /// <returns>The captured pixels.</returns>
    public Frame Capture() => Frame.Capture(Window);

    /// <summary>The bounds of a visual in window coordinates.</summary>
    /// <param name="visual">The visual to locate.</param>
    /// <returns>The bounds.</returns>
    public Rect BoundsOf(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);

        Point? origin = visual.TranslatePoint(default, Window);
        Assert.True(origin is not null, "The visual is not in this window.");
        return new Rect(origin!.Value, visual.Bounds.Size);
    }

    /// <inheritdoc />
    public void Dispose() => Window.Close();
}

/// <summary>The colours DESIGN.md names, as the renderer produces them.</summary>
internal static class Ink
{
    /// <summary>Light <c>Surface</c>.</summary>
    public static readonly Color Surface = Color.Parse("#FFFFFFFF");

    /// <summary>Light <c>SurfaceMuted</c>.</summary>
    public static readonly Color SurfaceMuted = Color.Parse("#FFFAFAFA");

    /// <summary>Light <c>TextPrimary</c>, which is also the focus ring.</summary>
    public static readonly Color TextPrimary = Color.Parse("#FF0A0A0A");

    /// <summary>Light <c>TextSecondary</c>, which is also the meter's threshold tick.</summary>
    public static readonly Color TextSecondary = Color.Parse("#FF666666");

    /// <summary>Light <c>Border</c>, which is also the hover step on a muted ground.</summary>
    public static readonly Color Border = Color.Parse("#FFEAEAEA");

    /// <summary>Whether a colour is dark enough to be ink rather than ground.</summary>
    /// <param name="colour">The colour to test.</param>
    /// <returns>True for anything at or below a quarter brightness.</returns>
    public static bool IsInk(Color colour) =>
        colour.A > 0x40 && colour.R < 0x60 && colour.G < 0x60 && colour.B < 0x60;

    /// <summary>Whether two colours are the same to within a tolerance per channel.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <param name="tolerance">The permitted difference per channel.</param>
    /// <returns>True when every channel is within tolerance.</returns>
    public static bool Near(Color left, Color right, int tolerance = 2) =>
        Math.Abs(left.R - right.R) <= tolerance
        && Math.Abs(left.G - right.G) <= tolerance
        && Math.Abs(left.B - right.B) <= tolerance
        && Math.Abs(left.A - right.A) <= tolerance;
}
