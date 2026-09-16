#if WINDOWS

using System.Runtime.InteropServices;
using Altim.UI.Views;
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

    /// <summary>The cursor's position in physical pixels, or null when Windows refused.</summary>
    public static PixelPoint? CursorPosition()
    {
        POINT point;
        return GetCursorPos(&point) ? new PixelPoint(point.X, point.Y) : null;
    }

    /// <summary>
    /// Who owns the window that currently holds the foreground.
    /// </summary>
    /// <returns>
    /// <see cref="PopupForegroundOwner.Self"/>, <see cref="PopupForegroundOwner.Tray"/> or
    /// <see cref="PopupForegroundOwner.Other"/> when Windows named a window, and
    /// <see cref="PopupForegroundOwner.Unknown"/> while it will not — which is a state to
    /// re-read a moment later rather than a verdict, because activation moving from one
    /// window to another passes through it.
    /// </returns>
    public static PopupForegroundOwner ForegroundOwner()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return PopupForegroundOwner.Unknown;
        }

        uint processId = 0;
        _ = GetWindowThreadProcessId(foreground, &processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return PopupForegroundOwner.Self;
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
