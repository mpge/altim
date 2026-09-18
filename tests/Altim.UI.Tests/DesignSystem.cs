using Altim.UI.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    /// Builds the tray panel window: the AltimPopupWindow theme, a 320 panel inside a
    /// window that is wider by the shadow inset on both sides.
    /// </summary>
    /// <param name="transparent">
    /// Whether to ask the compositor for transparency. Without it the shadow cannot be
    /// drawn, so the theme drops it and paints the inset in the panel ground instead.
    /// </param>
    /// <param name="variant">The theme variant to render under.</param>
    /// <param name="content">The panel content.</param>
    /// <returns>An unshown window.</returns>
    public static Window PopupWindow(
        bool transparent,
        ThemeVariant? variant = null,
        Control? content = null)
    {
        _ = Ensure();
        Application app = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(
            app.Resources.TryGetResource("AltimPopupWindow", ThemeVariant.Light, out object? theme),
            "AltimPopupWindow does not resolve.");

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            RequestedThemeVariant = variant ?? ThemeVariant.Dark,
            Theme = Assert.IsType<ControlTheme>(theme),
            Content = content ?? new TextBlock { Text = "Usage has reset." },
        };

        if (transparent)
        {
            window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        }

        return window;
    }

    /// <summary>
    /// Renders a control and asserts it is on screen: arranged to a real size the frame
    /// covers, and painting something of its own inside that size.
    /// </summary>
    /// <param name="content">The control to host.</param>
    /// <param name="variant">The theme variant to render under.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    /// <remarks>
    /// The frame's own size is the window's, which the caller passed in, so reading it back
    /// asserts nothing about <paramref name="content"/> at all: a control that measured to
    /// zero and painted nothing satisfied it. What is read instead is the control's arranged
    /// bounds and the pixels inside them, against the frame the same empty window produces.
    /// Anything the control drew - a ground, a border, a glyph - is a difference there; a
    /// control that drew nothing is not.
    /// </remarks>
    public static void AssertRenders(
        Control content,
        ThemeVariant? variant = null,
        double width = 320d,
        double height = 160d)
    {
        (Frame frame, Rect bounds) = CaptureLaidOut(content, variant, width, height);
        Frame ground = CaptureGround(variant, width, height);

        Assert.True(
            Ink(frame, ground, bounds) > 0,
            $"The control was arranged at {bounds} and painted nothing there: "
                + $"{frame.Describe(bounds)}.");
    }

    /// <summary>
    /// Renders a control and asserts it is arranged but paints nothing at all, which is the
    /// documented picture for a control given no room to draw in.
    /// </summary>
    /// <param name="content">The control to host.</param>
    /// <param name="variant">The theme variant to render under.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    /// <remarks>
    /// The counterpart to <see cref="AssertRenders"/>, and the reason there are two of them:
    /// a blanket "it must paint ink" would be wrong for a control whose contract is a blank
    /// picture. This holds that contract rather than waiving it - a blot drawn where there is
    /// no room to draw a reading fails here.
    /// </remarks>
    public static void AssertRendersNothing(
        Control content,
        ThemeVariant? variant = null,
        double width = 320d,
        double height = 160d)
    {
        (Frame frame, Rect bounds) = CaptureLaidOut(content, variant, width, height);
        Frame ground = CaptureGround(variant, width, height);

        int painted = Ink(frame, ground, bounds);
        Assert.True(
            painted == 0,
            $"The control painted {painted} pixels inside {bounds}, where the picture is "
                + $"blank by design: {frame.Describe(bounds)}.");
    }

    /// <summary>
    /// Renders two controls and asserts they produce the same picture, neither of them
    /// empty.
    /// </summary>
    /// <param name="first">The control the claim is about.</param>
    /// <param name="second">The control it is claimed to look like.</param>
    /// <param name="variant">The theme variant to render under.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    public static void AssertRendersAlike(
        Control first,
        Control second,
        ThemeVariant? variant = null,
        double width = 320d,
        double height = 160d)
    {
        (Frame frame, Rect bounds, Frame other, Rect theirs) =
            CapturePair(first, second, variant, width, height);

        Assert.Equal(bounds, theirs);

        int apart = frame.DifferenceWith(other, bounds);
        Assert.True(
            apart == 0,
            $"The two controls painted different pictures in {apart} of the pixels inside "
                + $"{bounds}: {frame.Describe(bounds)} against {other.Describe(bounds)}.");
    }

    /// <summary>
    /// Renders two controls and asserts they produce different pictures, neither of them
    /// empty.
    /// </summary>
    /// <param name="first">The control the claim is about.</param>
    /// <param name="second">The control it is claimed not to look like.</param>
    /// <param name="variant">The theme variant to render under.</param>
    /// <param name="width">The window width.</param>
    /// <param name="height">The window height.</param>
    public static void AssertRendersUnlike(
        Control first,
        Control second,
        ThemeVariant? variant = null,
        double width = 320d,
        double height = 160d)
    {
        (Frame frame, Rect bounds, Frame other, Rect theirs) =
            CapturePair(first, second, variant, width, height);

        Assert.Equal(bounds, theirs);
        Assert.True(
            frame.DifferenceWith(other, bounds) > 0,
            $"The two controls painted the same picture inside {bounds}: "
                + $"{frame.Describe(bounds)}.");
    }

    /// <summary>
    /// Renders both controls and the empty window they were hosted in, and asserts each of
    /// them painted something. Without that an "alike" claim is satisfied by two blank
    /// pictures, and an "unlike" one by a blank picture beside a drawn one.
    /// </summary>
    private static (Frame First, Rect Bounds, Frame Second, Rect Theirs) CapturePair(
        Control first,
        Control second,
        ThemeVariant? variant,
        double width,
        double height)
    {
        (Frame frame, Rect bounds) = CaptureLaidOut(first, variant, width, height);
        (Frame other, Rect theirs) = CaptureLaidOut(second, variant, width, height);
        Frame ground = CaptureGround(variant, width, height);

        Assert.True(Ink(frame, ground, bounds) > 0, "The first control painted nothing.");
        Assert.True(Ink(other, ground, theirs) > 0, "The second control painted nothing.");

        return (frame, bounds, other, theirs);
    }

    /// <summary>
    /// Hosts a control, captures the frame and reports where the control was actually
    /// arranged. The window is closed and the platform bitmap disposed before this returns.
    /// </summary>
    private static (Frame Frame, Rect Bounds) CaptureLaidOut(
        Control content,
        ThemeVariant? variant,
        double width,
        double height)
    {
        ArgumentNullException.ThrowIfNull(content);
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

            Frame frame = Frame.Capture(window);
            Point? origin = content.TranslatePoint(default, window);
            Assert.True(origin is not null, "The control never reached the window hosting it.");

            return (frame, new Rect(origin!.Value, content.Bounds.Size));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The frame the same window produces with nothing in it.</summary>
    private static Frame CaptureGround(ThemeVariant? variant, double width, double height)
    {
        _ = Ensure();

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            Width = width,
            Height = height,
            RequestedThemeVariant = variant ?? ThemeVariant.Light,
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return Frame.Capture(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// How many pixels inside a control's own bounds the control changed, having first
    /// asserted those bounds are a real area that the frame actually covers.
    /// </summary>
    private static int Ink(Frame frame, Frame ground, Rect bounds)
    {
        Assert.True(
            bounds.Width > 0d && bounds.Height > 0d,
            $"The control measured to {bounds.Width}x{bounds.Height}, so there is nothing on "
                + "screen to read.");
        Assert.True(
            bounds.X >= -0.5d
                && bounds.Y >= -0.5d
                && bounds.Right <= frame.Width + 0.5d
                && bounds.Bottom <= frame.Height + 0.5d,
            $"The control was arranged at {bounds}, which the {frame.Width}x{frame.Height} "
                + "frame does not cover, so part of it was never on screen.");

        return frame.DifferenceWith(ground, bounds);
    }
}
