namespace Altim.Core.Models;

/// <summary>
/// Which rendering of the tray icon a host should display. The choice is about
/// contrast against the tray background, not about the application theme.
/// </summary>
public enum TrayIconVariant
{
    /// <summary>
    /// Let the host decide. macOS uses the template image and lets the menu bar tint
    /// it, and Linux hands the icon to the desktop theme. This is the default, and on
    /// those two platforms it is the only correct value.
    /// </summary>
    Automatic = 0,

    /// <summary>
    /// A dark glyph, for a light tray background.
    /// </summary>
    Dark = 1,

    /// <summary>
    /// A light glyph, for a dark tray background.
    /// </summary>
    Light = 2,
}
