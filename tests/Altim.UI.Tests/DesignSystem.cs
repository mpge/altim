using Altim.UI.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Shared plumbing for the design system tests: one merge of the Altim dictionary into the
/// headless application, and one way to put a control on screen and prove it painted.
/// </summary>
internal static class DesignSystem
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Merges <see cref="AltimTheme"/> into the running application's resources, once.
    /// Application resources are searched ahead of application styles, so this is also the
    /// arrangement the app uses to win over the base theme's control themes.
    /// </summary>
    /// <returns>The merged dictionary.</returns>
    public static AltimTheme Ensure()
    {
        lock (Gate)
        {
            Application app = Assert.IsAssignableFrom<Application>(Application.Current);

            // Keyed off the live application rather than a static flag: the headless host
            // is free to build more than one Application over a run, and a static flag
            // would leave every application after the first without a design system.
            foreach (IResourceProvider merged in app.Resources.MergedDictionaries)
            {
                if (merged is AltimTheme existing)
                {
                    return existing;
                }
            }

            var theme = new AltimTheme();
            app.Resources.MergedDictionaries.Add(theme);
            return theme;
        }
    }

    /// <summary>
    /// Loads the design system dictionary on its own, without touching the application.
    /// Resource resolution tests use this so one test cannot see another test's state.
    /// </summary>
    /// <returns>A freshly loaded dictionary.</returns>
    public static AltimTheme LoadStandalone() => new();

    /// <summary>
    /// Hosts a control in a headless window, forces a frame, and returns it. The caller
    /// disposes the bitmap: an undisposed frame takes the renderer down on Linux.
    /// </summary>
    /// <param name="content">The control to host.</param>
    /// <param name="variant">The theme variant to render under.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    /// <returns>The captured frame, which the caller must dispose.</returns>
    public static WriteableBitmap Render(
        Control content,
        ThemeVariant? variant = null,
        double width = 320d,
        double height = 160d)
    {
        _ = Ensure();

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            Width = width,
            Height = height,
            RequestedThemeVariant = variant ?? ThemeVariant.Light,
            Content = content,
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            return frame;
        }
        finally
        {
            // The content stays attached: closing the window releases the platform
            // surface but leaves the logical tree, so a test can still inspect the
            // template that was applied to it.
            window.Close();
        }
    }

    /// <summary>
    /// Renders a control and disposes the frame, asserting only that a frame arrived with
    /// real pixels in it.
    /// </summary>
    /// <param name="content">The control to host.</param>
    /// <param name="variant">The theme variant to render under.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    public static void AssertRenders(
        Control content,
        ThemeVariant? variant = null,
        double width = 320d,
        double height = 160d)
    {
        using WriteableBitmap frame = Render(content, variant, width, height);
        Assert.True(frame.PixelSize.Width > 0, "The captured frame has no width.");
        Assert.True(frame.PixelSize.Height > 0, "The captured frame has no height.");
    }
}
