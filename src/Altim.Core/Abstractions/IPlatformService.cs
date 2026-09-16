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
    /// Raised when the machine is about to sleep. The scheduler pauses on it, so no timer
    /// tick and no filesystem hint is acted on across a sleep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window between this and the machine actually suspending is short — Windows
    /// allows about two seconds — so a handler stops work rather than starting any.
    /// </para>
    /// <para>
    /// <b>It is not guaranteed to arrive, and it is not the display going off.</b> A modern
    /// standby machine can sleep without sending the classic suspend broadcast, so a wake
    /// may arrive with no suspend before it; callers treat this as a hint that lets them do
    /// less, never as a precondition for <see cref="SystemResumed"/>. Platforms deliberately
    /// do not raise it for a display blanking, because a screen that has switched off does
    /// not mean the machine has stopped working, and an agent running against a dark monitor
    /// must still be recorded.
    /// </para>
    /// </remarks>
    event EventHandler? SystemSuspending;

    /// <summary>
    /// Raised when the machine wakes from sleep. The scheduler pauses on
    /// <see cref="SystemSuspending"/> and refreshes once here, so a wake produces one
    /// refresh rather than a backlog.
    /// </summary>
    /// <remarks>
    /// Implementations coalesce, so a wake reported by two sources raises this once.
    /// </remarks>
    event EventHandler? SystemResumed;

    /// <summary>
    /// Raised when the operating system light or dark preference changes. On Linux the
    /// portal may answer late, so this can fire some time after start-up.
    /// </summary>
    event EventHandler? ThemeChanged;
}
