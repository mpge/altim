namespace Altim.UI.Views;

/// <summary>Who owns the window that took the foreground away from the popup panel.</summary>
public enum PopupForegroundOwner
{
    /// <summary>
    /// Nobody, yet. Windows has no foreground window, or will not name its class: the brief
    /// state between one window losing activation and the next gaining it.
    /// </summary>
    Unknown = 0,

    /// <summary>One of Altim's own windows — the dashboard, or an overlay of the panel's own.</summary>
    Self = 1,

    /// <summary>The taskbar that holds the notification area, or the overflow flyout.</summary>
    Tray = 2,

    /// <summary>Somebody else's window, including the desktop.</summary>
    Other = 3,
}

/// <summary>What to do about a deactivation.</summary>
public enum PopupDismissalDecision
{
    /// <summary>Leave the panel open.</summary>
    Keep = 0,

    /// <summary>Nothing is decidable yet; ask again in a moment.</summary>
    Recheck = 1,

    /// <summary>Hide the panel.</summary>
    Dismiss = 2,
}

/// <summary>
/// Whether a deactivation is the user clicking away, as decided from the window that took
/// the foreground — with no window, no screen and no operating system in it.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN.md says the panel closes on deactivate, and for a long time it did not: clicking
/// the tray icon makes the shell's taskbar the foreground window and deactivates the panel,
/// so a handler that closed on every deactivation closed the panel a moment before the same
/// click reopened it — a flicker where the user asked for a toggle. The guard that fixed
/// that answered "not a real deactivation" to a foreground window it could not identify,
/// including no foreground window at all.
/// </para>
/// <para>
/// <b>That unidentifiable state is the normal one.</b> Windows clears the foreground while
/// activation moves between two windows, and the deactivation message arrives inside it, so
/// the guard swallowed nearly every genuine click-away as well: measured over one
/// application log, the panel closed by deactivation once against fifty-six closes by
/// clicking the tray icon a second time. The panel sat on top of whatever the user had
/// switched to until they went back to the tray.
/// </para>
/// <para>
/// So the unknown answer is not an answer. Three states are worth acting on and the fourth
/// is worth waiting out:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Self</b> — the foreground belongs to this process. Opening the dashboard from the
/// panel deactivates it, and so does any overlay that takes focus; neither is the user
/// leaving. Keep.
/// </description></item>
/// <item><description>
/// <b>Tray</b> — the foreground is the taskbar that carries the notification area or the
/// overflow flyout it lives in. This is the click that is about to toggle the panel shut by
/// itself. Keep, and let the toggle do it.
/// </description></item>
/// <item><description>
/// <b>Other</b> — any other window, the desktop included. Dismiss.
/// </description></item>
/// <item><description>
/// <b>Unknown</b> — the transition. Ask again shortly rather than guessing: by then either
/// the tray click has already closed the panel, or the window the user switched to owns the
/// foreground and names itself. Only when it will still not settle is the panel dismissed,
/// because the panel is no longer the active window and DESIGN.md says that closes it.
/// </description></item>
/// </list>
/// <para>
/// The classes below are the whole of the identification, and they are deliberately few. A
/// class name is evidence only where it can mean one thing:
/// <c>Windows.UI.Core.CoreWindow</c> was in this list and is the class of every packaged
/// application's window, so clicking Settings or Calculator read as clicking the tray.
/// <c>Shell_SecondaryTrayWnd</c> is a taskbar on another monitor, which carries no
/// notification area and so can never be the click that toggles the panel — it is somebody
/// else's window like any other.
/// </para>
/// </remarks>
public static class PopupDismissal
{
    /// <summary>
    /// The window classes that can carry the click that toggles the panel.
    /// </summary>
    /// <remarks>
    /// <c>Shell_TrayWnd</c> is the taskbar that owns the notification area; the icon is a
    /// child of it, and a child never holds the foreground. The other two are the overflow
    /// the icon sits in until it is promoted — the XAML flyout on Windows 11 22H2 and later,
    /// and the classic window before it. They are the same two classes
    /// <c>TrayOverflow</c> hit tests against, for the same reason.
    /// </remarks>
    private static readonly string[] TrayWindowClassNames =
    [
        "Shell_TrayWnd",
        "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow",
    ];

    /// <summary>The window classes <see cref="Classify"/> reads as the tray.</summary>
    public static IReadOnlyList<string> TrayWindowClasses => TrayWindowClassNames;

    /// <summary>True when a window class belongs to the notification area rather than to an application.</summary>
    /// <param name="windowClass">The foreground window's class name.</param>
    public static bool IsTrayWindowClass(ReadOnlySpan<char> windowClass)
    {
        foreach (string candidate in TrayWindowClassNames)
        {
            if (windowClass.Equals(candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Names the owner of the window that took the foreground.
    /// </summary>
    /// <param name="hasForegroundWindow">False when the system reported no foreground window at all.</param>
    /// <param name="isOwnProcess">True when that window belongs to this process.</param>
    /// <param name="windowClass">
    /// That window's class name, or empty when it could not be read — which is the same
    /// amount of information as no window.
    /// </param>
    /// <returns>Who owns it, or <see cref="PopupForegroundOwner.Unknown"/> while nothing does.</returns>
    public static PopupForegroundOwner Classify(bool hasForegroundWindow, bool isOwnProcess, ReadOnlySpan<char> windowClass)
    {
        if (!hasForegroundWindow)
        {
            return PopupForegroundOwner.Unknown;
        }

        // Asked first, because an overlay of our own is the one case where the class name
        // would be misleading: Avalonia's own popup windows carry a framework class, not
        // Altim's.
        if (isOwnProcess)
        {
            return PopupForegroundOwner.Self;
        }

        if (windowClass.IsEmpty)
        {
            return PopupForegroundOwner.Unknown;
        }

        return IsTrayWindowClass(windowClass) ? PopupForegroundOwner.Tray : PopupForegroundOwner.Other;
    }

    /// <summary>
    /// Turns an owner into what the host should do about it.
    /// </summary>
    /// <param name="owner">Who took the foreground.</param>
    /// <param name="canRecheck">
    /// True while there is still a re-check left in the budget. When it runs out an
    /// <see cref="PopupForegroundOwner.Unknown"/> foreground dismisses rather than keeps:
    /// waiting forever is how the panel came to sit over other windows in the first place.
    /// </param>
    /// <returns>Keep the panel, ask again, or hide it.</returns>
    public static PopupDismissalDecision Decide(PopupForegroundOwner owner, bool canRecheck) => owner switch
    {
        PopupForegroundOwner.Self or PopupForegroundOwner.Tray => PopupDismissalDecision.Keep,
        PopupForegroundOwner.Unknown when canRecheck => PopupDismissalDecision.Recheck,
        _ => PopupDismissalDecision.Dismiss,
    };

    /// <summary>One phrase per owner, for the log line a dismissal leaves behind.</summary>
    /// <param name="owner">Who took the foreground.</param>
    public static string Describe(PopupForegroundOwner owner) => owner switch
    {
        PopupForegroundOwner.Self => "another Altim window took the foreground",
        PopupForegroundOwner.Tray => "the tray took the foreground",
        PopupForegroundOwner.Other => "another window took the foreground",
        _ => "the foreground never settled",
    };
}
