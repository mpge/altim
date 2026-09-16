using Avalonia;
using Avalonia.Controls;

namespace Altim.UI.Views;

/// <summary>
/// The transparent room the popup window reserves around its panel for the one shadow.
/// </summary>
/// <remarks>
/// <para>
/// The inset is the difference between the window and the panel, and the design system sets
/// it — <c>AltimPopupShadowMargin</c>, which the <c>AltimPopupWindow</c> theme reads into
/// this property. It is a property rather than the resource read straight into the template
/// because the placement code trims it: the side facing the tray icon is transparent window
/// over the icon, and a window over a tray icon swallows the clicks meant for it.
/// </para>
/// <para>
/// So the token stays the design's value and this carries the value in force, which is the
/// token everywhere except the one edge currently pointing at a tray icon. Whoever changes
/// it owns the window's width too: the panel's own width is fixed, so the window is only the
/// right size for the inset it was last told about.
/// </para>
/// </remarks>
public static class PopupChrome
{
    /// <summary>The inset in force, in device-independent units.</summary>
    public static readonly AttachedProperty<Thickness> ShadowInsetProperty =
        AvaloniaProperty.RegisterAttached<Window, Thickness>("ShadowInset", typeof(PopupChrome));

    /// <summary>Reads the inset a window is currently reserving.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The inset in device-independent units.</returns>
    public static Thickness GetShadowInset(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.GetValue(ShadowInsetProperty);
    }

    /// <summary>Sets the inset a window reserves.</summary>
    /// <param name="window">The window.</param>
    /// <param name="value">The inset in device-independent units.</param>
    public static void SetShadowInset(Window window, Thickness value)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SetValue(ShadowInsetProperty, value);
    }
}
