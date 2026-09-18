using Avalonia;
using Avalonia.Controls;
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

        // The view's own arranged size is read before the frame is captured, so a root that
        // collapsed to nothing is reported as that rather than as a window that happens to
        // have painted one flat colour. It is the one thing a frame's size cannot be asked
        // about: the frame is the window's, and the window is whatever the caller asked for.
        Render(
            window,
            shown =>
            {
                Rect bounds = At(content, shown);
                Assert.True(
                    bounds.Width > 0d && bounds.Height > 0d,
                    $"The view measured to {bounds.Width}x{bounds.Height}, so nothing it "
                        + "holds reached the screen.");
            },
            inspect);
    }

    /// <summary>Renders a window that is already built, and runs an inspection over it.</summary>
    /// <param name="window">The window to show.</param>
    /// <param name="inspect">What to check while it is on screen.</param>
    /// <remarks>
    /// The guard used to read the captured frame's own size, which is the size the caller
    /// asked the window for: it reduced to a number the test itself supplied and said nothing
    /// about what was hosted in it. What is read instead is whether the frame carries more
    /// than one colour. A window whose content measured to nothing, or threw its content
    /// away, is left as its own flat ground and fails here.
    /// </remarks>
    public static void Render(Window window, Action<Window> inspect) =>
        Render(window, null, inspect);

    /// <summary>
    /// Renders a window, checks what was laid out before the frame is taken, and runs an
    /// inspection over what is on screen.
    /// </summary>
    /// <param name="window">The window to show.</param>
    /// <param name="arranged">What to check once layout has run and before the capture.</param>
    /// <param name="inspect">What to check while it is on screen.</param>
    private static void Render(Window window, Action<Window>? arranged, Action<Window> inspect)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(inspect);

        _ = DesignSystem.Ensure();

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            arranged?.Invoke(window);

            // Frame.Capture ticks the render timer and disposes the platform bitmap before it
            // returns: an undisposed frame takes the renderer down.
            Frame frame = Frame.Capture(window);
            Assert.False(
                frame.IsFlat(new Rect(0d, 0d, frame.Width, frame.Height)),
                $"The window is one flat {frame.At(0, 0)}, so nothing was drawn on it.");

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
    /// <remarks>
    /// This walks <c>IsEffectivelyVisible</c>, which stays true for content arranged past a
    /// window edge. It answers "the page holds this line", not "a reader can see it": for
    /// that, see <see cref="AssertReachable"/>.
    /// </remarks>
    public static bool Shows(Visual root, string text) => Lines(root).Contains(text);

    /// <summary>
    /// Scrolls the page to a line of text and reports where the line then sits in the window.
    /// </summary>
    /// <param name="root">The window the view is hosted in.</param>
    /// <param name="text">The exact line.</param>
    /// <returns>The line's bounds in window coordinates.</returns>
    public static Rect Reach(Window root, string text)
    {
        ArgumentNullException.ThrowIfNull(root);

        TextBlock? block = null;
        foreach (TextBlock candidate in Visible<TextBlock>(root))
        {
            if (string.Equals(candidate.Text, text, StringComparison.Ordinal))
            {
                block = candidate;
                break;
            }
        }

        Assert.True(block is not null, $"\"{text}\" is not on the page at all.");

        block!.BringIntoView();
        Dispatcher.UIThread.RunJobs();

        return At(block, root);
    }

    /// <summary>
    /// Asserts each line named can be reached: the page scrolls to it, and it then lies
    /// inside the window rather than past one of its edges.
    /// </summary>
    /// <param name="root">The window the view is hosted in.</param>
    /// <param name="lines">The exact lines.</param>
    /// <remarks>
    /// <see cref="Shows"/> cannot fail for a size reason, so a test that inflates its host
    /// until the whole page fits is asserting nothing about reach. This is the assertion that
    /// can fail: hosted at a size a reader would actually have, a section that the page
    /// cannot scroll to is a section nobody can read.
    /// </remarks>
    public static void AssertReachable(Window root, params string[] lines)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(lines);

        foreach (string line in lines)
        {
            Rect at = Reach(root, line);

            // Half a pixel of slack: a line laid out on a fractional offset is still on
            // screen, and a line past an edge misses by far more than that.
            Assert.True(
                new Rect(root.ClientSize).Inflate(0.5d).Contains(at),
                $"\"{line}\" is arranged at {at} in a {root.ClientSize} window, so the page "
                    + "cannot be scrolled to it.");
        }
    }

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

    /// <summary>A visual's bounds in the coordinates of the window holding it.</summary>
    /// <param name="visual">The visual to locate.</param>
    /// <param name="root">The window it is hosted in.</param>
    /// <returns>The bounds.</returns>
    private static Rect At(Visual visual, Window root)
    {
        Point? origin = visual.TranslatePoint(default, root);
        Assert.True(origin is not null, "The visual is not in this window.");
        return new Rect(origin!.Value, visual.Bounds.Size);
    }
}
