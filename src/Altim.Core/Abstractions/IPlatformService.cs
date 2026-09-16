using Altim.Core.Models;

namespace Altim.Core.Abstractions;

/// <summary>
/// Everything the host operating system does differently: tray host, screen
/// geometry, theme and power. One implementation per platform, resolved once at
/// start-up.
/// </summary>
public interface IPlatformService
{
    /// <summary>
    /// The tray or menu bar presence. Owned by the platform service and disposed with
    /// it, so callers must not dispose it themselves.
    /// </summary>
    ITrayHost Tray { get; }

    /// <summary>
    /// The tray icon rectangle in physical pixels, for anchoring the popup.
    /// </summary>
    /// <returns>
    /// The icon rectangle, or <see langword="null"/> when this platform cannot report
    /// one. Null is the normal answer on Linux, where the StatusNotifierItem protocol
    /// has no geometry at all, and it is also returned on Windows and macOS when the
    /// icon is hidden in an overflow. A null sends the caller to the next positioning
    /// tier, cursor position and then the working area corner; it is never treated as
    /// an empty rectangle at the origin.
    /// </returns>
    ValueTask<PixelRect?> GetTrayAnchorAsync();

    /// <summary>
    /// Raised when the machine wakes from sleep. The scheduler pauses on suspend and
    /// refreshes once here, so a wake produces one refresh rather than a backlog.
    /// </summary>
    event EventHandler? SystemResumed;

    /// <summary>
    /// Raised when the operating system light or dark preference changes. On Linux the
    /// portal may answer late, so this can fire some time after start-up.
    /// </summary>
    event EventHandler? ThemeChanged;
}
