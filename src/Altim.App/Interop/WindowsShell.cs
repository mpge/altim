#if WINDOWS

using System.Diagnostics;
using System.Runtime.InteropServices;
using Altim.UI.Views;
using Avalonia;

namespace Altim.App.Interop;

/// <summary>
/// The handful of things the composition root has to ask Windows directly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CursorPosition"/> is the popup's second positioning tier. The tray host
/// already falls back to the click point when the shell reports the icon with no geometry,
/// but it reports no anchor at all when the icon sits in the Windows 11 overflow, and the
/// cursor is then the only thing that knows where the user actually clicked.
/// </para>
/// <para>
/// <see cref="ForegroundOwner"/> reads the three facts that decide whether a deactivation is
/// the user clicking away: whether there is a foreground window, whether it is one of ours,
/// and what its class is. It decides nothing itself — <see cref="PopupDismissal"/> does, and
/// it lives in <c>Altim.UI</c> beside <see cref="PopupPlacement"/> because, like the
/// placement arithmetic, it is a pure function that can be asserted without a screen.
/// </para>
/// </remarks>
internal static unsafe partial class WindowsShell
{
    /// <summary>Longest window class name Windows will register, plus a terminator.</summary>
    private const int ClassNameCapacity = 257;

    /// <summary>The owner of a window, for <c>GetWindow</c>.</summary>
    private const uint GW_OWNER = 4;

    /// <summary>The top-level ancestor of a window, for <c>GetAncestor</c>.</summary>
    private const uint GA_ROOT = 2;

    /// <summary>The primary and secondary mouse buttons, in whichever order the user has them.</summary>
    private const int VK_LBUTTON = 0x01;

    /// <inheritdoc cref="VK_LBUTTON" />
    private const int VK_RBUTTON = 0x02;

    /// <summary>
    /// The bit <c>GetAsyncKeyState</c> sets when the key has been pressed since the previous
    /// call, which is what makes a quarter-second poll catch a click that lasted a fiftieth
    /// of one.
    /// </summary>
    private const short PressedSinceLastCall = 0x0001;

    /// <summary>
    /// How far up an owner chain to walk before giving up. Avalonia nests an overlay one
    /// level; anything deeper than a handful is a cycle somebody else built.
    /// </summary>
    private const int MaxOwnerDepth = 8;

    /// <summary>The cursor's position in physical pixels, or null when Windows refused.</summary>
    public static PixelPoint? CursorPosition()
    {
        POINT point;
        return GetCursorPos(&point) ? new PixelPoint(point.X, point.Y) : null;
    }

    /// <summary>
    /// Hands this process's right to set the foreground window to the Altim that already
    /// owns the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second launch signals the running instance to show its panel, and the running
    /// instance then cannot bring it to the front: Windows only lets the process that
    /// currently owns the foreground set it, and that is this one, the launch that is about
    /// to exit. The panel appeared without ever becoming active, which meant it never
    /// received a deactivation either, so clicking away did not dismiss it and a topmost
    /// panel sat over whatever the user was doing.
    /// </para>
    /// <para>
    /// <c>AllowSetForegroundWindow</c> is the documented way to hand the right over. It is
    /// given to the specific process rather than to <c>ASFW_ANY</c>, so nothing else on the
    /// machine gains anything from Altim being launched twice.
    /// </para>
    /// </remarks>
    public static void GrantForegroundToRunningInstance()
    {
        try
        {
            using Process self = Process.GetCurrentProcess();

            foreach (Process other in Process.GetProcessesByName(self.ProcessName))
            {
                using (other)
                {
                    if (other.Id != Environment.ProcessId)
                    {
                        _ = AllowSetForegroundWindow((uint)other.Id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // A process table this launch cannot read costs the hand-over and nothing else:
            // the running instance still shows its panel, it simply may not be able to take
            // the foreground with it.
        }
    }

    /// <summary>
    /// Who owns the window that currently holds the foreground.
    /// </summary>
    /// <param name="panel">
    /// The panel window's handle, or <see cref="IntPtr.Zero"/> when it has none yet. It is
    /// what separates an overlay the panel owns, which is still the panel, from another
    /// window of Altim's, which is the dashboard and is a dismissal like any other.
    /// </param>
    /// <returns>
    /// <see cref="PopupForegroundOwner.Self"/>, <see cref="PopupForegroundOwner.Tray"/> or
    /// <see cref="PopupForegroundOwner.Other"/> when Windows named a window, and
    /// <see cref="PopupForegroundOwner.Unknown"/> while it will not — which is a state to
    /// re-read a moment later rather than a verdict, because activation moving from one
    /// window to another passes through it.
    /// </returns>
    public static PopupForegroundOwner ForegroundOwner(IntPtr panel) =>
        ClassifyForeground(GetForegroundWindow(), panel);

    /// <summary>
    /// The window that currently holds the foreground, as a handle.
    /// </summary>
    /// <returns>
    /// The handle, or <see cref="IntPtr.Zero"/> while nothing holds it — which is the state
    /// Windows passes through whenever activation moves between two windows.
    /// </returns>
    /// <remarks>
    /// Read as a handle rather than as an answer because the panel watches for the
    /// foreground <em>changing</em>, not for it belonging to somebody else. A panel surfaced
    /// by a second launch can open while another application already holds the foreground,
    /// and dismissing it for that would take away the panel the user has just asked for a
    /// quarter of a second after it appeared.
    /// </remarks>
    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    /// <summary>
    /// Who owns a particular window.
    /// </summary>
    /// <param name="foreground">The window to classify, usually the foreground one.</param>
    /// <param name="panel">The panel window's handle, or <see cref="IntPtr.Zero"/>.</param>
    public static PopupForegroundOwner ClassifyForeground(IntPtr foreground, IntPtr panel)
    {
        if (foreground == IntPtr.Zero)
        {
            return PopupForegroundOwner.Unknown;
        }

        uint processId = 0;
        _ = GetWindowThreadProcessId(foreground, &processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return PopupDismissal.Classify(
                hasForegroundWindow: true,
                isOwnProcess: true,
                ReadOnlySpan<char>.Empty,
                IsPanelOrOwnedByIt(foreground, panel));
        }

        Span<char> buffer = stackalloc char[ClassNameCapacity];
        int length;
        fixed (char* start = buffer)
        {
            length = GetClassNameW(foreground, start, buffer.Length);
        }

        return PopupDismissal.Classify(
            hasForegroundWindow: true,
            isOwnProcess: false,
            length > 0 ? buffer[..length] : ReadOnlySpan<char>.Empty);
    }

    /// <summary>
    /// Who owns the window a click has just landed on, or
    /// <see cref="PopupForegroundOwner.Unknown"/> when no click has landed since the last
    /// time this was asked.
    /// </summary>
    /// <param name="panel">The panel window's handle, or <see cref="IntPtr.Zero"/>.</param>
    /// <remarks>
    /// <para>
    /// The foreground moving is the ordinary way a click-away is noticed, and it does not
    /// cover the case this exists for: a panel surfaced by a second launch that could not
    /// take the foreground is sitting over a window that <em>already has</em> it, so the
    /// user clicking back into their own work moves nothing at all. Nothing is deactivated,
    /// nothing changes hands, and a panel watching only for a change waits for ever.
    /// </para>
    /// <para>
    /// A click is cheap to notice without a hook. <c>GetAsyncKeyState</c> reports whether
    /// the button has been pressed since the last time this process asked, so a poll a
    /// quarter of a second apart cannot miss one, and the window under the cursor is then
    /// classified exactly as a foreground window would be — so a click on the panel keeps
    /// it, a click on the tray icon leaves the toggle to do the closing, and a click on
    /// anything else dismisses.
    /// </para>
    /// </remarks>
    public static PopupForegroundOwner ClickedOn(IntPtr panel)
    {
        if (!TakePendingClick())
        {
            return PopupForegroundOwner.Unknown;
        }

        POINT point;
        if (!GetCursorPos(&point))
        {
            return PopupForegroundOwner.Unknown;
        }

        return ClassifyForeground(GetAncestor(WindowFromPoint(point), GA_ROOT), panel);
    }

    /// <summary>
    /// Reads and clears whatever presses have accumulated, so the next
    /// <see cref="ClickedOn"/> answers about clicks from here on.
    /// </summary>
    /// <remarks>
    /// Called when the panel opens. Without it the first look would report the click that
    /// opened the panel — which lands on the tray, so it would be kept rather than acted on,
    /// but it would still be an answer about the past.
    /// </remarks>
    public static void ForgetPendingClicks() => _ = TakePendingClick();

    private static bool TakePendingClick()
    {
        // Both are read every time, and deliberately not short circuited: the bit is
        // cleared by the read, so skipping one would leave a press to be reported later.
        bool primary = (GetAsyncKeyState(VK_LBUTTON) & PressedSinceLastCall) != 0;
        bool secondary = (GetAsyncKeyState(VK_RBUTTON) & PressedSinceLastCall) != 0;
        return primary || secondary;
    }

    /// <summary>
    /// Whether a window is the panel or is owned by it, directly or through another owner.
    /// </summary>
    /// <param name="window">The foreground window.</param>
    /// <param name="panel">The panel window's handle.</param>
    /// <remarks>
    /// An Avalonia overlay is a top level of its own owned by the window it belongs to, so
    /// the owner chain is what identifies it; its class name is the framework's and says
    /// nothing about which of Altim's windows it came from. With no panel handle the answer
    /// is yes, which is the behaviour this had before the dashboard was told apart from an
    /// overlay.
    /// </remarks>
    private static bool IsPanelOrOwnedByIt(IntPtr window, IntPtr panel)
    {
        if (panel == IntPtr.Zero)
        {
            return true;
        }

        IntPtr current = window;
        for (int depth = 0; current != IntPtr.Zero && depth < MaxOwnerDepth; depth++)
        {
            if (current == panel)
            {
                return true;
            }

            current = GetWindow(current, GW_OWNER);
        }

        return false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(POINT* point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, uint* processId);

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(IntPtr window, char* buffer, int capacity);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint relationship);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}

#endif
