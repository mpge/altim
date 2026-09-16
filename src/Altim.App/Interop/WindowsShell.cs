#if WINDOWS

using System.Runtime.InteropServices;
using Avalonia;

namespace Altim.App.Interop;

/// <summary>
/// The two questions the composition root has to ask Windows directly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CursorPosition"/> is the popup's second positioning tier. The tray host
/// already falls back to the click point when the shell reports the icon with no geometry,
/// but it reports no anchor at all when the icon sits in the Windows 11 overflow, and the
/// cursor is then the only thing that knows where the user actually clicked.
/// </para>
/// <para>
/// <see cref="ForegroundBelongsToTrayOrSelf"/> is the fix for the click that would otherwise
/// open and immediately close the panel. Clicking the tray icon deactivates the panel, so a
/// handler that closed on every deactivation would close the panel the click is about to
/// reopen. The shell's own windows and Altim's own windows are therefore not real
/// deactivations.
/// </para>
/// </remarks>
internal static unsafe partial class WindowsShell
{
    /// <summary>
    /// Window classes that belong to the notification area rather than to an application.
    /// The last two are the Windows 11 overflow flyout, which is a XAML island rather than
    /// the classic overflow window.
    /// </summary>
    private static readonly string[] TrayClasses =
    [
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "TrayNotifyWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
    ];

    /// <summary>The cursor's position in physical pixels, or null when Windows refused.</summary>
    public static PixelPoint? CursorPosition()
    {
        POINT point;
        return GetCursorPos(&point) ? new PixelPoint(point.X, point.Y) : null;
    }

    /// <summary>
    /// True when the window that took the foreground is the shell's tray, the overflow
    /// flyout, or one of Altim's own windows — none of which is a reason to close the panel.
    /// </summary>
    /// <remarks>
    /// An unknown foreground window, which is what a race during the transition reports,
    /// is also treated as "not a real deactivation": leaving the panel open one click too
    /// long is a smaller failure than making the tray icon look broken.
    /// </remarks>
    public static bool ForegroundBelongsToTrayOrSelf()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return true;
        }

        uint processId = 0;
        _ = GetWindowThreadProcessId(foreground, &processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return true;
        }

        Span<char> buffer = stackalloc char[128];
        int length;
        fixed (char* start = buffer)
        {
            length = GetClassNameW(foreground, start, buffer.Length);
        }

        if (length <= 0)
        {
            return true;
        }

        ReadOnlySpan<char> name = buffer[..length];
        foreach (string candidate in TrayClasses)
        {
            if (name.Equals(candidate, StringComparison.Ordinal))
            {
                return true;
            }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}

#endif
