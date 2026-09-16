using Altim.UI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Every control Altim restyles has to actually take the Altim theme, render under both
/// variants, and keep the focus ring it was given. The focus ring is a template part rather
/// than a setter precisely so this test can find it: a style cannot remove a part.
/// </summary>
public sealed class ControlThemeTests
{
    /// <summary>The controls the design system restyles, one factory each.</summary>
    public static TheoryData<string> ControlNames =>
    [
        "Button",
        "PrimaryButton",
        "QuietButton",
        "IconButton",
        "TextButton",
        "DestructiveButton",
        "ToggleSwitch",
        "ComboBox",
        "TextBox",
        "ScrollViewer",
        "ListBox",
        "SidebarListBox",
        "Separator",
        "VerticalSeparator",
        "ToolTip",
        "Meter",
        "UsageTape",
        "Panel",
        "ProviderCard",
        "Chip",
        "Pill",
        "BrandMark",
        "Sidebar",
        "StatusDot",
        "LegendDot",
    ];

    /// <summary>Every restyled control renders a frame in Light and in Dark.</summary>
    /// <param name="name">The control to build and render.</param>
    [AvaloniaTheory]
    [MemberData(nameof(ControlNames))]
    public void EveryControlRendersInBothVariants(string name)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            DesignSystem.AssertRenders(Build(name), variant, width: 320d, height: 160d);
        }
    }

    /// <summary>
    /// The Altim control themes win over the base theme's. Application resources are
    /// searched ahead of application styles, which is what makes that true regardless of
    /// the order the styles were added in.
    /// </summary>
    [AvaloniaFact]
    public void AltimControlThemesWinOverTheBaseTheme()
    {
        var button = new Button { Content = "Retry" };

        using (WriteableBitmap frame = DesignSystem.Render(button))
        {
            Assert.True(frame.PixelSize.Width > 0);
        }

        Assert.Equal(32d, button.MinHeight);
        Assert.NotNull(Part<Border>(button, "PART_FocusRing"));
        Assert.NotNull(Part<Border>(button, "PART_Body"));
    }

    /// <summary>
    /// Hit targets are the ones in DESIGN.md: 32px for buttons and inputs, 28px for rows.
    /// </summary>
    [AvaloniaFact]
    public void HitTargetsMeetTheMinimums()
    {
        var button = new Button { Content = "Retry" };
        var input = new TextBox();
        var combo = new ComboBox();
        var row = new ListBoxItem { Content = "Session" };
        var toggle = new ToggleSwitch { Content = "Start with the system" };

        var stack = new StackPanel();
        stack.Children.Add(button);
        stack.Children.Add(input);
        stack.Children.Add(combo);
        stack.Children.Add(row);
        stack.Children.Add(toggle);

        using WriteableBitmap frame = DesignSystem.Render(stack, width: 320d, height: 240d);
        Assert.True(frame.PixelSize.Width > 0);

        Assert.True(button.Bounds.Height >= 32d, $"Button is {button.Bounds.Height} tall.");
        Assert.True(input.Bounds.Height >= 32d, $"TextBox is {input.Bounds.Height} tall.");
        Assert.True(combo.Bounds.Height >= 32d, $"ComboBox is {combo.Bounds.Height} tall.");
        Assert.True(row.Bounds.Height >= 28d, $"ListBoxItem is {row.Bounds.Height} tall.");
        Assert.True(toggle.Bounds.Height >= 28d, $"ToggleSwitch is {toggle.Bounds.Height} tall.");
    }

    /// <summary>
    /// Every focusable control keeps a focus ring part. DESIGN.md says the ring is always
    /// visible and never removed, so its absence from a template is the failure to catch.
    /// </summary>
    [AvaloniaFact]
    public void EveryFocusableControlKeepsItsFocusRing()
    {
        var button = new Button { Content = "Retry" };
        var input = new TextBox();
        var combo = new ComboBox();
        var row = new ListBoxItem { Content = "Session" };
        var toggle = new ToggleSwitch();

        var stack = new StackPanel();
        stack.Children.Add(button);
        stack.Children.Add(input);
        stack.Children.Add(combo);
        stack.Children.Add(row);
        stack.Children.Add(toggle);

        using WriteableBitmap frame = DesignSystem.Render(stack, width: 320d, height: 240d);
        Assert.True(frame.PixelSize.Width > 0);

        foreach (Control control in stack.Children.Cast<Control>())
        {
            Border ring = Assert.IsType<Border>(Part<Border>(control, "PART_FocusRing"));

            // 2px thick, offset 2px outside the control: the Border sits at -(2 + 2).
            Assert.Equal(new Thickness(2d), ring.BorderThickness);
            Assert.Equal(new Thickness(-4d), ring.Margin);
            Assert.False(ring.IsVisible);
        }
    }

    /// <summary>
    /// The scroll viewer template is wired: content taller than the viewport gives the
    /// vertical scrollbar something to scroll.
    /// </summary>
    [AvaloniaFact]
    public void ScrollViewerDrivesItsScrollBars()
    {
        var viewer = new ScrollViewer
        {
            Content = new Border { Height = 1000d, Width = 100d },
        };

        using WriteableBitmap frame = DesignSystem.Render(viewer, width: 200d, height: 120d);
        Assert.True(frame.PixelSize.Width > 0);

        Assert.True(viewer.Extent.Height > viewer.Viewport.Height, "The content did not overflow.");

        ScrollBar? vertical = Part<ScrollBar>(viewer, "PART_VerticalScrollBar");
        Assert.NotNull(vertical);
        Assert.NotNull(Part<Track>(vertical, "PART_Track"));
        Assert.True(viewer.ScrollBarMaximum.Y > 0d, "There is nothing to scroll to.");
    }

    /// <summary>The drop down opens and paints without the template throwing.</summary>
    [AvaloniaFact]
    public void ComboBoxOpensItsDropDown()
    {
        var combo = new ComboBox
        {
            ItemsSource = new[] { "System", "Light", "Dark" },
            SelectedIndex = 0,
            IsDropDownOpen = true,
        };

        using WriteableBitmap frame = DesignSystem.Render(combo, width: 240d, height: 160d);
        Assert.True(frame.PixelSize.Width > 0);

        Popup popup = Assert.IsType<Popup>(Part<Popup>(combo, "PART_Popup"));
        Border body = Assert.IsType<Border>(popup.Child);
        Assert.Equal("PART_PopupBody", body.Name);

        // The drop down closes with the window it was hosted in, so its state is not
        // asserted here: what this test proves is that opening it did not throw and that
        // the popup carries the panel shape rather than a bare list.
        Assert.Equal(new CornerRadius(8d), body.CornerRadius);
        Assert.Equal(1, body.BoxShadow.Count);
    }

    /// <summary>
    /// The track is a pill, and the knob fits it. The radius has to be half the height or
    /// the ends are not semicircles: at the control radius of 6 a 16 tall track reads as a
    /// rounded rectangle. The knob inset is a hairline rather than a spacing step because
    /// the track border already takes a pixel from each side - at a 2 inset the knob
    /// measured 10 and sat squashed inside a track one border too small for it.
    /// </summary>
    [AvaloniaFact]
    public void TheToggleTrackIsAPillWithAKnobThatFitsIt()
    {
        var toggle = new ToggleSwitch { Content = "Start with the system" };

        using WriteableBitmap frame = DesignSystem.Render(toggle, width: 240d, height: 48d);
        Assert.True(frame.PixelSize.Width > 0);

        Border track = Assert.IsType<Border>(Part<Border>(toggle, "PART_Track"));
        Panel knobs = Assert.IsType<Panel>(Part<Panel>(toggle, "PART_MovingKnobs"));
        Border knob = Assert.IsType<Border>(Part<Border>(toggle, "PART_Knob"));

        Assert.Equal(28d, track.Bounds.Width, 6);
        Assert.Equal(16d, track.Bounds.Height, 6);

        // Half the height on every corner: the ends are semicircles, not soft corners.
        Assert.Equal(new CornerRadius(track.Bounds.Height / 2d), track.CornerRadius);
        Assert.Equal(new CornerRadius(knob.Bounds.Height / 2d), knob.CornerRadius);

        // 16 track - 2 border - 2 inset = 12, which is the knob's own size.
        Assert.Equal(12d, knobs.Bounds.Height, 6);
        Assert.Equal(12d, knobs.Bounds.Width, 6);

        // The travel is one spacing step: 28 - 12 - 2 border - 2 inset = 12.
        Assert.Equal(12d, track.Bounds.Width - knobs.Bounds.Width - 4d, 6);
    }

    /// <summary>The toggle moves its knob across and colours the track when checked.</summary>
    [AvaloniaFact]
    public void ToggleSwitchMovesItsKnob()
    {
        var toggle = new ToggleSwitch { Content = "Start with the system", IsChecked = true };

        using WriteableBitmap frame = DesignSystem.Render(toggle, width: 240d, height: 48d);
        Assert.True(frame.PixelSize.Width > 0);

        Panel knobs = Assert.IsType<Panel>(Part<Panel>(toggle, "PART_MovingKnobs"));
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, knobs.HorizontalAlignment);
        Assert.NotNull(Part<Panel>(toggle, "PART_SwitchKnob"));
    }

    /// <summary>
    /// The popup panel window theme renders the shape DESIGN.md specifies: a 320 wide
    /// PANEL, radius 8, one border, one shadow. The window is wider than the panel by the
    /// shadow inset on both sides, which is the transparent room the shadow falls into.
    /// </summary>
    /// <remarks>
    /// The old form of this test asserted the WINDOW was 320 and passed while the panel
    /// measured 272: the inset ate 48px and the thing the user sees was the wrong width.
    /// The panel's laid out width is the assertion that cannot be satisfied by a wrong
    /// panel inside a right window.
    /// </remarks>
    [AvaloniaFact]
    public void PopupWindowThemeRenders()
    {
        Window window = DesignSystem.PopupWindow(transparent: true);

        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

            using WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            Assert.Equal(WindowTransparencyLevel.Transparent, window.ActualTransparencyLevel);

            Border panel = Assert.IsType<Border>(Part<Border>(window, "PART_PopupPanel"));
            Assert.Equal(new CornerRadius(12d), panel.CornerRadius);
            Assert.Equal(new Thickness(1d), panel.BorderThickness);
            Assert.Equal(1, panel.BoxShadow.Count);

            // The panel is the 320. The window is 320 plus the inset on both sides.
            Assert.Equal(320d, panel.Bounds.Width, 6);
            Assert.Equal(new Thickness(24d, 16d, 24d, 32d), panel.Margin);
            Assert.Equal(368d, window.Width);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Without transparency the shadow goes and the panel stays exactly where it was: the
    /// inset is still there, painted in the fallback ground rather than left as the black
    /// an unpainted transparent surface renders as. The panel is 320 in both states.
    /// </summary>
    [AvaloniaFact]
    public void PopupWindowDropsItsShadowWithoutTransparency()
    {
        Window window = DesignSystem.PopupWindow(transparent: false);

        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

            using WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            Assert.Equal(WindowTransparencyLevel.None, window.ActualTransparencyLevel);

            Border panel = Assert.IsType<Border>(Part<Border>(window, "PART_PopupPanel"));
            Assert.Equal(320d, panel.Bounds.Width, 6);
            Assert.Equal(new Thickness(24d, 16d, 24d, 32d), panel.Margin);
            Assert.Equal(0, panel.BoxShadow.Count);

            // The fallback ground is the panel's own colour, so the inset is never black.
            Border fallback = Assert.IsType<Border>(Part<Border>(window, "PART_TransparencyFallback"));
            var ground = Assert.IsAssignableFrom<ISolidColorBrush>(fallback.Background);
            var expected = Assert.IsAssignableFrom<ISolidColorBrush>(panel.Background);
            Assert.Equal(expected.Color, ground.Color);
        }
        finally
        {
            window.Close();
        }
    }


    private static T? Part<T>(Visual root, string name)
        where T : Visual =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(v => v.Name == name);

    private static Control Build(string name) => name switch
    {
        "Button" => new Button { Content = "Retry" },
        "PrimaryButton" => Themed(new Button { Content = "Open Altim" }, "AltimPrimaryButton"),
        "QuietButton" => Themed(new Button { Content = "Clear history" }, "AltimQuietButton"),
        "IconButton" => Themed(new Button { Content = "S" }, "AltimIconButton"),
        "TextButton" => Themed(new Button { Content = "Open Altim" }, "AltimTextButton"),
        "DestructiveButton" => Themed(new Button { Content = "Delete history" }, "AltimDestructiveButton"),
        "ToggleSwitch" => new ToggleSwitch { Content = "Start with the system", IsChecked = true },
        "ComboBox" => new ComboBox { ItemsSource = new[] { "System", "Light", "Dark" }, SelectedIndex = 0 },
        "TextBox" => new TextBox { PlaceholderText = "Threshold" },
        "ScrollViewer" => new ScrollViewer { Content = new Border { Height = 400d } },
        "ListBox" => new ListBox { ItemsSource = new[] { "Session", "Weekly", "Opus weekly" } },
        "SidebarListBox" => Themed(
            new ListBox { ItemsSource = new[] { "Overview", "Activity", "History", "Settings" }, SelectedIndex = 0 },
            "AltimSidebarListBox"),
        "Separator" => new Separator(),
        "VerticalSeparator" => Themed(new Separator(), "AltimVerticalSeparator"),
        "ToolTip" => new ToolTip { Content = "Resets at 8:00 PM" },
        "Meter" => new Meter { Value = 62d, Threshold = 80d },
        "UsageTape" => new UsageTape
        {
            Series = [new UsageTapeSeries("Claude", [10d, 30d, 25d, 60d, 80d])],
        },
        "Panel" => Themed(
            new Border { Child = new TextBlock { Text = "Usage history" } },
            "AltimPanel"),
        "ProviderCard" => Themed(
            new Border { Child = new TextBlock { Text = "Claude" } },
            "AltimProviderCard"),
        "Chip" => Themed(
            new Border { Child = new TextBlock { Text = "claude-opus-4" } },
            "AltimChip"),
        "Pill" => Themed(new Border { Child = new TextBlock { Text = "Live" } }, "AltimPill"),
        "BrandMark" => Themed(new Border(), "AltimBrandMark"),
        "Sidebar" => Themed(
            new Border { Child = new TextBlock { Text = "Overview" } },
            "AltimSidebar"),
        "StatusDot" => Themed(new Ellipse(), "AltimStatusDot"),
        "LegendDot" => Themed(new Ellipse(), "AltimLegendDot"),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown control."),
    };

    private static T Themed<T>(T control, string key)
        where T : Control
    {
        DesignSystem.Ensure();
        Application app = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(
            app.Resources.TryGetResource(key, ThemeVariant.Light, out object? theme),
            $"{key} does not resolve.");
        control.Theme = Assert.IsType<ControlTheme>(theme);
        return control;
    }
}
