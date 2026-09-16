using Altim.Core.Models;

namespace Altim.Core.Abstractions;

/// <summary>
/// The tray or menu bar presence, reduced to the smallest surface all three hosts can
/// satisfy: a Win32 <c>Shell_NotifyIcon</c> message-only window, a macOS
/// <c>NSStatusItem</c>, and Avalonia's own <c>TrayIcon</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here exposes a bitmap, a window handle, a menu object or a platform
/// coordinate type, because no two of the three agree on any of those. What they do
/// agree on is: an icon that can be present or absent, a tooltip string, a small set
/// of icon renderings, a primary activation that may or may not know where it
/// happened, and a flat native menu.
/// </para>
/// <para>
/// Every mutator is asynchronous because two of the three hosts must marshal to a
/// specific thread: Shell_NotifyIcon to the thread owning the message-only window,
/// and NSStatusItem to the main thread. Returning <see cref="ValueTask"/> lets a host
/// that is already on the right thread complete synchronously without allocating.
/// </para>
/// </remarks>
public interface ITrayHost : IDisposable
{
    /// <summary>
    /// True while the icon is in the tray. On Windows 11 a new icon is placed in the
    /// overflow by default, which still counts as visible: the icon exists, the user
    /// simply has to promote it.
    /// </summary>
    bool IsVisible { get; }

    /// <summary>Adds the icon to the tray. Does nothing when it is already there.</summary>
    /// <param name="ct">Cancels the call before the icon is added.</param>
    ValueTask ShowAsync(CancellationToken ct = default);

    /// <summary>Removes the icon from the tray. Does nothing when it is already absent.</summary>
    /// <param name="ct">Cancels the call before the icon is removed.</param>
    ValueTask HideAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the hover text. Hosts truncate to their own platform limit rather than
    /// failing, so callers may pass a longer string than a platform accepts.
    /// </summary>
    /// <param name="tooltip">The text to show. Empty removes the tooltip.</param>
    /// <param name="ct">Cancels the call before the tooltip is applied.</param>
    ValueTask SetTooltipAsync(string tooltip, CancellationToken ct = default);

    /// <summary>
    /// Chooses which rendering of the icon to display. A host that renders only one
    /// image, such as the macOS template icon, accepts every value and ignores it.
    /// </summary>
    /// <param name="variant">The rendering to display.</param>
    /// <param name="ct">Cancels the call before the icon is swapped.</param>
    ValueTask SetIconAsync(TrayIconVariant variant, CancellationToken ct = default);

    /// <summary>
    /// Presents the native menu. This is the fallback path for when the popup panel
    /// cannot be placed, which is the permanent situation on Linux and the documented
    /// escape hatch for an unverified macOS host.
    /// </summary>
    /// <param name="items">
    /// The entries, in order, flat. Use <see cref="TrayMenuItem.Separator"/> for a rule.
    /// </param>
    /// <param name="anchor">
    /// Where to place the menu, in physical pixels, or <see langword="null"/> when the
    /// caller has no anchor and the host should place it itself, which is the only
    /// possibility on Linux.
    /// </param>
    /// <param name="ct">Cancels the call before the menu is presented.</param>
    /// <remarks>
    /// The call completes once the menu has been handed to the platform, not when the
    /// user dismisses it; the selection arrives on <see cref="MenuItemInvoked"/>. A
    /// host that cannot raise a menu on demand, which is the case for Avalonia's
    /// <c>TrayIcon</c>, attaches these entries so the platform presents them on the
    /// next secondary activation.
    /// </remarks>
    ValueTask ShowMenuAsync(IReadOnlyList<TrayMenuItem> items, PixelRect? anchor, CancellationToken ct = default);

    /// <summary>
    /// Raised on primary activation of the icon: left click on Windows and Linux, and
    /// a click on the status button on macOS. Carries the anchor rectangle when the
    /// host knows it.
    /// </summary>
    event EventHandler<TrayClickEventArgs>? Clicked;

    /// <summary>
    /// Raised when the user picks an entry from the native menu. Never raised for a
    /// separator or a disabled entry.
    /// </summary>
    event EventHandler<TrayMenuItemInvokedEventArgs>? MenuItemInvoked;
}
