using Altim.UI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
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
        "QuietButton",
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
        "ProviderCard",
        "StatusDot",
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
    /// The popup panel window theme renders: 320 wide, radius 8, one border, one shadow.
    /// </summary>
    [AvaloniaFact]
    public void PopupWindowThemeRenders()
    {
        DesignSystem.Ensure();
        Application app = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(
            app.Resources.TryGetResource("AltimPopupWindow", ThemeVariant.Light, out object? theme));

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            RequestedThemeVariant = ThemeVariant.Dark,
            Theme = Assert.IsType<ControlTheme>(theme),
            Content = new TextBlock { Text = "Usage has reset." },
        };

        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

            using WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            Border panel = Assert.IsType<Border>(Part<Border>(window, "PART_PopupPanel"));
            Assert.Equal(new CornerRadius(8d), panel.CornerRadius);
            Assert.Equal(new Thickness(1d), panel.BorderThickness);
            Assert.Equal(1, panel.BoxShadow.Count);
            Assert.Equal(320d, window.Width);
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
        "QuietButton" => Themed(new Button { Content = "Clear history" }, "AltimQuietButton"),
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
        "ProviderCard" => Themed(
            new Border { Child = new TextBlock { Text = "Claude" } },
            "AltimProviderCard"),
        "StatusDot" => Themed(new Ellipse(), "AltimStatusDot"),
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
