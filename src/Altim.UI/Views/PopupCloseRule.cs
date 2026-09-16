using Avalonia.Controls;

namespace Altim.UI.Views;

/// <summary>What to do about a request to close the panel's window.</summary>
public enum PopupCloseAction
{
    /// <summary>Refuse the close and hide the panel instead. The window survives.</summary>
    Hide = 0,

    /// <summary>Let the close through. The process is going away with it.</summary>
    Close = 1,
}

/// <summary>
/// Whether a request to close the panel window is refused, decided from the reason alone —
/// with no window, no screen and no session in it.
/// </summary>
/// <remarks>
/// <para>
/// The panel is hidden and never closed for the life of the process. That is not an
/// optimisation: a tray-only application with no live top level cannot reach screen
/// geometry, and Windows only invalidates its cached screen list from a live window. So
/// anything that asks the window to close gets a hide — alt+F4 on a focused panel, for
/// instance — and the window stays.
/// </para>
/// <para>
/// <b>One caller must not be refused, and refusing it was a shipped defect.</b> Avalonia
/// asks every window to close while it is answering the operating system's session-end
/// query, and whether that close was cancelled is the answer it gives back. A panel that
/// cancels its own close therefore answers <c>WM_QUERYENDSESSION</c> with a veto, and the
/// user gets the shell's "this app is preventing you from signing out" screen with Altim
/// named on it. Reproduced on Windows 11 26200 by sending the query to the running process:
/// it returned 0.
/// </para>
/// <para>
/// The unknown answer is a hide rather than a close, deliberately. Hiding is the recoverable
/// mistake — the panel goes away and the window is still there to be shown again — while
/// closing on a guess costs the process the only top level it has, and every later open
/// positions itself against screens it can no longer ask about.
/// </para>
/// </remarks>
public static class PopupCloseRule
{
    /// <summary>
    /// Decides whether to refuse a close.
    /// </summary>
    /// <param name="reason">Why Avalonia says the window is closing.</param>
    /// <returns>
    /// <see cref="PopupCloseAction.Close"/> for a shutdown, which must be allowed through,
    /// and <see cref="PopupCloseAction.Hide"/> for everything else.
    /// </returns>
    public static PopupCloseAction Decide(WindowCloseReason reason) => reason switch
    {
        WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown => PopupCloseAction.Close,
        _ => PopupCloseAction.Hide,
    };

    /// <summary>One phrase per reason, for the log line the decision leaves behind.</summary>
    /// <param name="reason">Why Avalonia says the window is closing.</param>
    public static string Describe(WindowCloseReason reason) => reason switch
    {
        WindowCloseReason.OSShutdown => "the operating system is ending the session",
        WindowCloseReason.ApplicationShutdown => "Altim is shutting down",
        WindowCloseReason.OwnerWindowClosing => "an owner window is closing",
        WindowCloseReason.WindowClosing => "the window was asked to close",
        _ => "no reason was reported",
    };
}
