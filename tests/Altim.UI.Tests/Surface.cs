using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Puts a view on screen, proves it painted, and lets a test read what is actually visible.
/// </summary>
/// <remarks>
/// Inspection happens while the window is still open, so <c>IsEffectivelyVisible</c> means what
/// it says: a branch collapsed by a binding is not counted as shown. Every captured frame is
/// disposed before the window closes, because an undisposed frame takes the renderer down.
/// </remarks>
internal static class Surface
{
    /// <summary>Hosts a view in a window, renders it, and runs an inspection over it.</summary>
    /// <param name="content">The view to host.</param>
    /// <param name="inspect">What to check while the view is on screen.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    public static void Show(Control content, Action<Window> inspect, double width = 360d, double height = 900d)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(inspect);

        _ = DesignSystem.Ensure();

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            Width = width,
            Height = height,
            Content = content,
        };

        Render(window, inspect);
    }

    /// <summary>Renders a window that is already built, and runs an inspection over it.</summary>
    /// <param name="window">The window to show.</param>
    /// <param name="inspect">What to check while it is on screen.</param>
    public static void Render(Window window, Action<Window> inspect)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(inspect);

        _ = DesignSystem.Ensure();

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

            using (WriteableBitmap? frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                Assert.True(frame.PixelSize.Width > 0, "The captured frame has no width.");
                Assert.True(frame.PixelSize.Height > 0, "The captured frame has no height.");
            }

            inspect(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every visible element of a kind below a root.</summary>
    /// <typeparam name="T">The kind to find.</typeparam>
    /// <param name="root">Where to look.</param>
    public static IReadOnlyList<T> Visible<T>(Visual root)
        where T : Visual
    {
        ArgumentNullException.ThrowIfNull(root);

        List<T> found = [];
        foreach (Visual visual in root.GetVisualDescendants())
        {
            if (visual is T match && match.IsEffectivelyVisible)
            {
                found.Add(match);
            }
        }

        return found;
    }

    /// <summary>Every line of text actually on screen below a root.</summary>
    /// <param name="root">Where to look.</param>
    public static IReadOnlyList<string> Lines(Visual root)
    {
        List<string> lines = [];
        foreach (TextBlock block in Visible<TextBlock>(root))
        {
            if (block.Text is { Length: > 0 } text)
            {
                lines.Add(text);
            }
        }

        return lines;
    }

    /// <summary>Whether a line of text is on screen below a root.</summary>
    /// <param name="root">Where to look.</param>
    /// <param name="text">The exact line.</param>
    public static bool Shows(Visual root, string text) => Lines(root).Contains(text);

    /// <summary>How many times a line of text is on screen below a root.</summary>
    /// <param name="root">Where to look.</param>
    /// <param name="text">The exact line.</param>
    public static int Count(Visual root, string text)
    {
        int count = 0;
        foreach (string line in Lines(root))
        {
            if (string.Equals(line, text, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
