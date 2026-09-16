using System.Collections.ObjectModel;
using Altim.UI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The assertions that read the screen rather than the object graph.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these covers a failure the rest of the suite was blind to, because every
/// one of them was true of the object graph and false of the picture: a focus ring that
/// existed as a template part and was clipped away before it reached a pixel, a popup
/// shadow set as a BoxShadow and clipped by the panel, a panel measured at 272 inside a
/// window the tests asserted was 320, a toggle whose template said nothing about words
/// while the framework printed "On" beside it, a hover brush that resolved correctly and
/// was the exact colour of the ground it was drawn on.
/// </para>
/// <para>
/// Every captured frame is copied out and the platform bitmap disposed before the assertion
/// runs - see <see cref="Frame"/> - because an undisposed frame takes the renderer down.
/// </para>
/// </remarks>
public sealed class PixelTests
{
    /// <summary>Every control whose theme hosts the focus ring.</summary>
    public static TheoryData<string> Focusable =>
    [
        "Button",
        "TextBox",
        "ComboBox",
        "ListBoxItem",
        "ToggleSwitch",
    ];

    /// <summary>
    /// The ring reaches pixels. It is drawn outside the control's own bounds, which is
    /// exactly what <c>TemplatedControl</c>'s default <c>ClipToBounds</c> erased: the part
    /// was in the template, the part was visible, and nothing was painted.
    /// </summary>
    /// <param name="name">The control to focus.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Focusable))]
    public void TheFocusRingPaintsOutsideTheControl(string name)
    {
        Control control = Build(name);
        using PixelHost host = PixelHost.Show(
            new Border { Padding = new Thickness(20d), Child = control },
            width: 240d,
            height: 96d);

        Frame before = host.Capture();

        Assert.True(control.Focus(NavigationMethod.Tab), $"{name} did not take focus.");

        Frame after = host.Capture();

        Border ring = PixelHost.Part<Border>(control, "PART_FocusRing");
        Assert.True(ring.IsVisible, $"{name} did not show its focus ring.");

        Rect ringBounds = host.BoundsOf(ring);
        Rect controlBounds = host.BoundsOf(control);

        // The ring is offset outside whatever it rings, which for the toggle is the pill
        // rather than the whole row. Its right upright is outside the control in every
        // case, so that is the one that proves ClipToBounds is not eating it.
        Assert.True(
            ringBounds.Right > controlBounds.Right,
            $"{name} draws its ring inside its own bounds, so this proves nothing.");

        // 2px wide, clear of the radius 8 corners at each end.
        var upright = new Rect(
            ringBounds.Right - 2d,
            ringBounds.Y + 10d,
            2d,
            Math.Max(1d, ringBounds.Height - 20d));

        Assert.Equal(0, before.Count(upright, Ink.IsInk));

        int painted = after.Count(upright, Ink.IsInk);
        int area = (int)(upright.Width * upright.Height);

        // Solid, not dashed: a 1,2 dash pattern would cover about a third of the upright.
        Assert.True(
            painted >= area * 9 / 10,
            $"{name} painted {painted} of {area} ring pixels: {after.Describe(upright)}");
    }

    /// <summary>
    /// Nothing is drawn on the control's own edge while it is focused. The framework's
    /// focus adorner is a 1px black dashed rectangle drawn there, in the adorner layer, and
    /// it appeared beside the Altim ring until the themes nulled <c>FocusAdorner</c>.
    /// </summary>
    [AvaloniaFact]
    public void NoDashedAdornerIsDrawnOnAFocusedControl()
    {
        var button = new Button
        {
            Content = "Retry",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        using PixelHost host = PixelHost.Show(
            new Border { Padding = new Thickness(20d), Child = button },
            width: 240d,
            height: 96d);

        Assert.True(button.Focus(NavigationMethod.Tab));
        Frame frame = host.Capture();

        Rect bounds = host.BoundsOf(button);

        // The control's own border is AltimBorder, #EAEAEA. Ink on that row is not ours.
        var top = new Rect(bounds.X + 12d, bounds.Y, bounds.Width - 24d, 1d);
        var bottom = new Rect(bounds.X + 12d, bounds.Bottom - 1d, bounds.Width - 24d, 1d);

        Assert.True(
            frame.Count(top, Ink.IsInk) == 0,
            $"Ink on the button's own top edge: {frame.Describe(top)}");
        Assert.True(
            frame.Count(bottom, Ink.IsInk) == 0,
            $"Ink on the button's own bottom edge: {frame.Describe(bottom)}");

        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(button);
        Assert.True(layer is null || layer.Children.Count == 0, "The adorner layer is not empty.");
    }

    /// <summary>
    /// The panel is 320 device pixels wide on the screen. The suite used to assert the
    /// WINDOW was 320, which passed while the shadow inset ate 48 of them and the panel the
    /// user actually sees measured 272.
    /// </summary>
    [AvaloniaFact]
    public void ThePopupPanelMeasuresThreeHundredAndTwentyPixels()
    {
        Window window = DesignSystem.PopupWindow(transparent: true, ThemeVariant.Light);
        using PixelHost host = PixelHost.Show(window);

        Frame frame = host.Capture();
        Border panel = PixelHost.Part<Border>(window, "PART_PopupPanel");
        Rect bounds = host.BoundsOf(panel);

        Assert.Equal(320d, bounds.Width, 6);

        // A row through the middle of the panel, clear of its rounded corners. The panel is
        // the only solid thing on it: the inset around it is transparent room for the
        // shadow, and the shadow is at most 12% alpha, so half alpha separates the two
        // cleanly while tolerating the antialiased outer column of a rounded rectangle.
        int row = (int)Math.Round(bounds.Y + (bounds.Height / 2d));
        int span = frame.SpanAt(row, colour => colour.A >= 0x80);

        Assert.True(
            span == 320,
            $"The panel spans {span} pixels on row {row}, not 320: "
                + frame.Describe(new Rect(0d, row, frame.Width, 1d), take: 8));
    }

    /// <summary>
    /// The one shadow in the system reaches pixels. It is drawn outside the panel's bounds,
    /// so clipping the panel erased it while every property assertion about the BoxShadow
    /// still passed.
    /// </summary>
    [AvaloniaFact]
    public void ThePopupShadowPaintsOutsideThePanel()
    {
        Window window = DesignSystem.PopupWindow(transparent: true, ThemeVariant.Light);
        using PixelHost host = PixelHost.Show(window);

        Frame frame = host.Capture();
        Border panel = PixelHost.Part<Border>(window, "PART_PopupPanel");
        Rect bounds = host.BoundsOf(panel);

        // Directly under the panel, where a shadow offset 8 down and blurred 24 must land.
        var under = new Rect(bounds.X + 40d, bounds.Bottom + 1d, bounds.Width - 80d, 8d);

        // The far bottom corner of the window, past the blur, is the control sample.
        var corner = new Rect(0d, frame.Height - 2d, 6d, 2d);

        double shadow = frame.MeanAlpha(under);
        double clear = frame.MeanAlpha(corner);

        Assert.True(
            shadow > clear + 8d,
            $"No shadow under the panel. Under: {frame.Describe(under)}. Clear: {frame.Describe(corner)}.");
        Assert.True(
            frame.Any(under, colour => colour.A is > 0 and < 0xFF),
            $"The area under the panel is not a blur: {frame.Describe(under)}");
    }

    /// <summary>
    /// The toggle prints no words. <c>ToggleSwitch</c> defaults <c>OnContent</c> and
    /// <c>OffContent</c> to the strings "On" and "Off" and its default template draws them
    /// beside the pill; the state is the pill, so both are nulled and both presenters are
    /// gone from the Altim template.
    /// </summary>
    /// <param name="isChecked">The state to render.</param>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheToggleDrawsNoOnOrOffText(bool isChecked)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
        };

        using PixelHost host = PixelHost.Show(
            new Border { Padding = new Thickness(20d), Child = toggle },
            width: 240d,
            height: 64d);

        Frame frame = host.Capture();

        Rect bounds = host.BoundsOf(toggle);
        Rect track = host.BoundsOf(PixelHost.Part<Border>(toggle, "PART_Track"));

        // Everything to the left of the pill, which is where the framework sets its words.
        var beside = new Rect(bounds.X, bounds.Y, Math.Max(0d, track.X - bounds.X - 2d), bounds.Height);
        Assert.True(beside.Width > 40d, "There is no room beside the pill to test.");

        Assert.True(
            frame.Count(beside, colour => !Ink.Near(colour, Ink.Surface)) == 0,
            $"Something is drawn beside the pill: {frame.Describe(beside)}");

        Assert.Null(toggle.OnContent);
        Assert.Null(toggle.OffContent);
    }

    /// <summary>
    /// Hover is visible on a muted ground. The hover brush used to be <c>SurfaceMuted</c>,
    /// which is the popup's and the sidebar's own ground, so hovering a row on either of
    /// them painted that row in the colour it already was.
    /// </summary>
    /// <param name="name">The muted surface to hover over.</param>
    [AvaloniaTheory]
    [InlineData("SidebarItem")]
    [InlineData("QuietButton")]
    public void HoverIsVisibleOnAMutedGround(string name)
    {
        Control target;
        Control content;

        // The sidebar's ground belongs to the Border around the list rather than to the
        // list, because the brand header above it and the footer block below it stand on
        // the same ground. Standing the list on its own would put it on Surface, which is
        // not the ground the hover step was derived against.
        if (name == "SidebarItem")
        {
            var sidebar = new ListBox
            {
                ItemsSource = new[] { "Overview", "Activity", "History" },
                Theme = Resolve("AltimSidebarListBox"),
            };

            content = new Border { Theme = Resolve("AltimSidebar"), Child = sidebar };
            target = sidebar;
        }
        else
        {
            var button = new Button
            {
                Content = "Clear history",
                Theme = Resolve("AltimQuietButton"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            content = new Border
            {
                Background = Resolve<IBrush>("AltimSurfaceMutedBrush"),
                Padding = new Thickness(20d),
                Child = button,
            };

            target = button;
        }

        using PixelHost host = PixelHost.Show(content, width: 240d, height: 140d);

        Control hovered = name == "SidebarItem"
            ? Assert.IsType<ListBoxItem>(((ListBox)target).ContainerFromIndex(1))
            : target;

        Rect bounds = host.BoundsOf(hovered);
        var inside = new Rect(bounds.X + 10d, bounds.Y + 4d, 8d, Math.Max(1d, bounds.Height - 8d));

        Frame before = host.Capture();
        Assert.True(
            before.Count(inside, colour => Ink.Near(colour, Ink.SurfaceMuted)) > 0,
            $"The row is not standing on a muted ground: {before.Describe(inside)}");

        host.Window.MouseMove(bounds.Center, RawInputModifiers.None);
        Frame after = host.Capture();

        // A control on a muted ground steps off it to say it is hovered. The sidebar
        // lifts to Surface, because it keeps the step towards Border for the selected row;
        // everything else takes Border, the next step along the same ramp.
        Color expected = name == "SidebarItem" ? Ink.Surface : Ink.Border;
        Assert.True(
            after.Count(inside, colour => Ink.Near(colour, expected)) > 0,
            $"Hover is invisible on a muted ground: {after.Describe(inside)}");
        Assert.True(
            after.DifferenceWith(before, inside) > 0,
            "Hovering changed nothing.");
    }

    /// <summary>
    /// The tape repaints when the collection it was given is mutated in place. It used to
    /// compare the list by reference and never hear about it, so a page that added a
    /// provider row kept drawing the old chart.
    /// </summary>
    [AvaloniaFact]
    public void TheTapeRepaintsWhenItsCollectionIsMutatedInPlace()
    {
        ObservableCollection<UsageTapeSeries> series = [];
        var tape = new UsageTape { Series = series };

        using PixelHost host = PixelHost.Show(tape, width: 320d, height: 160d);

        Frame empty = host.Capture();
        Rect plot = host.BoundsOf(tape);

        series.Add(new UsageTapeSeries("Claude", [5d, 40d, 20d, 95d, 60d]));
        Frame drawn = host.Capture();

        Assert.True(
            drawn.DifferenceWith(empty, plot) > 200,
            $"The tape did not repaint after its collection was added to. It holds "
                + $"{tape.Series!.Count} of {series.Count} series.");

        series.Clear();
        Frame cleared = host.Capture();

        Assert.True(
            cleared.DifferenceWith(drawn, plot) > 200,
            "The tape did not repaint after its collection was cleared.");
    }

    /// <summary>
    /// What the tape holds is its own frozen copy, so mutating the list that produced it
    /// cannot change the picture behind the tape's back. Assigning and notifying are the
    /// only two ways in.
    /// </summary>
    [AvaloniaFact]
    public void TheTapeKeepsAFrozenCopyOfWhatItWasGiven()
    {
        List<UsageTapeSeries> given = [new UsageTapeSeries("Claude", [10d, 90d])];
        var tape = new UsageTape { Series = given };

        using PixelHost host = PixelHost.Show(tape, width: 320d, height: 160d);

        Frame before = host.Capture();
        Rect plot = host.BoundsOf(tape);

        Assert.NotSame(given, tape.Series);
        Assert.IsNotAssignableFrom<IList<UsageTapeSeries>>(tape.Series);

        given.Add(new UsageTapeSeries("Codex", [80d, 20d], UsageTapeEmphasis.Secondary));
        Frame after = host.Capture();

        Assert.Single(tape.Series!);
        Assert.Equal(0, after.DifferenceWith(before, plot));
    }

    /// <summary>
    /// A hairline is one whole device pixel at 125%. Unsnapped, a 1px rule is 1.25 device
    /// pixels: the rasteriser spreads it over two rows at partial coverage and the line
    /// reads as a grey smear whose weight depends on where it happened to land.
    /// </summary>
    [AvaloniaFact]
    public void HairlinesAreWholeDevicePixelsAtOneHundredAndTwentyFivePercent()
    {
        var meter = new Meter { Value = 0d, Threshold = 50d };
        var row = new StackPanel { Children = { meter } };

        using PixelHost host = PixelHost.Show(row, width: 200d, height: 40d);
        host.Window.SetRenderScaling(1.25d);

        Frame frame = host.Capture();
        Rect bounds = host.BoundsOf(meter);

        // The tick is TextSecondary on a Border track, both flat colours. Every pixel in
        // the tick column is one or the other; a smeared tick would be a blend of them.
        //
        // The band stops short of the rail's rounded ends on every side. Those ends are
        // antialiased against the page by design - the rail is a pill - so a band that
        // included them would be reporting the radius as a smear. The inset is the rail's
        // own radius, which is half its height, in device pixels.
        double radius = Meter.RailHeight / 2d * 1.25d;
        var band = new Rect(
            (bounds.X * 1.25d) + radius,
            (bounds.Y * 1.25d) + 2d,
            (bounds.Width * 1.25d) - (radius * 2d),
            Math.Max(1d, (bounds.Height * 1.25d) - 4d));

        int blended = frame.Count(
            band,
            colour => !Ink.Near(colour, Ink.Border, 6) && !Ink.Near(colour, Ink.TextSecondary, 6));

        Assert.True(
            blended == 0,
            $"The hairline was not snapped to device pixels: {frame.Describe(band, take: 6)}");
    }

    private static Control Build(string name) => name switch
    {
        "Button" => new Button { Content = "Retry" },
        "TextBox" => new TextBox { PlaceholderText = "Threshold" },
        "ComboBox" => new ComboBox { ItemsSource = new[] { "System", "Light", "Dark" }, SelectedIndex = 0 },
        "ListBoxItem" => new ListBoxItem { Content = "Session" },
        "ToggleSwitch" => new ToggleSwitch { Content = "Start with the system" },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown control."),
    };

    private static ControlTheme Resolve(string key) => PixelHost.Theme(key);

    private static T Resolve<T>(string key)
    {
        _ = DesignSystem.Ensure();
        Application app = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(
            app.Resources.TryGetResource(key, ThemeVariant.Light, out object? value),
            $"{key} does not resolve.");
        return Assert.IsAssignableFrom<T>(value);
    }
}
