using Altim.Core.Models;

namespace Altim.Core.Abstractions;

/// <summary>
/// Carries what a tray host knows about a primary activation.
/// </summary>
/// <param name="anchor">
/// The icon rectangle in physical pixels at the moment of the click, or
/// <see langword="null"/> when the host cannot report one. Null is permanent on
/// Linux, where the StatusNotifierItem protocol carries no geometry, and occurs on
/// Windows when the icon is in the overflow. A null must send the caller to the next
/// positioning tier, never be read as a rectangle at the screen origin.
/// </param>
public sealed class TrayClickEventArgs(PixelRect? anchor) : EventArgs
{
    /// <summary>
    /// The icon rectangle in physical pixels, or <see langword="null"/> when the host
    /// could not determine one.
    /// </summary>
    public PixelRect? Anchor { get; } = anchor;
}
