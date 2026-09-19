#if WINDOWS

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Altim.Core.Diagnostics;

namespace Altim.Platform.Windows.Interop;

/// <summary>
/// The window pair and the message loop the Windows tray presence is built on.
/// </summary>
/// <remarks>
/// <para>
/// There are two windows, on one dedicated thread, because no single window can do
/// both jobs.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="IconWindow"/> is message-only (<c>HWND_MESSAGE</c>) and owns the
/// notification area icon. It is invisible to window enumeration, never appears in
/// Alt+Tab, and costs nothing to keep alive.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="ShellWindow"/> is a hidden top-level window. Windows does not deliver
/// broadcast messages to message-only windows, and <c>TaskbarCreated</c> — the signal
/// that Explorer restarted and every tray icon must be re-added — is a broadcast. The
/// same window also owns the popup menu, because <c>SetForegroundWindow</c> and
/// <c>TrackPopupMenuEx</c> need a top-level owner, and receives the power setting
/// notifications used to detect a modern standby wake.
/// </description>
/// </item>
/// </list>
/// <para>
/// The thread runs a plain <c>GetMessage</c> loop, so it consumes no CPU at all while
/// idle: it is blocked in the kernel until a message arrives. Work is marshalled onto
/// it by posting a private message, never by polling a queue.
/// </para>
/// </remarks>
internal sealed unsafe class TrayWindow : IDisposable
{
    /// <summary>Message the shell posts for the notification icon.</summary>
    internal const uint WmTrayCallback = NativeMethods.WM_APP + 1;

    /// <summary>Private message that tells the loop to drain the work queue.</summary>
    private const uint WmWorkItem = NativeMethods.WM_APP + 2;

    private readonly ConcurrentQueue<WorkItem> _work = new();
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly Thread _thread;
    private readonly string _className;

    private GCHandle _self;
    private ushort _atom;
    private IntPtr _iconWindow;
    private IntPtr _shellWindow;
    private IntPtr _moduleHandle;
    private uint _taskbarCreated;
    private Exception? _startupError;
    private volatile bool _disposed;

    /// <summary>
    /// Creates both windows and starts the message loop. Returns once the windows
    /// exist, so callers may use <see cref="IconWindow"/> immediately.
    /// </summary>
    /// <exception cref="InvalidOperationException">The windows could not be created.</exception>
    public TrayWindow()
    {
        _className = "AltimTray+" + Guid.NewGuid().ToString("N");

        _thread = new Thread(ThreadMain)
        {
            Name = "Altim tray",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();

        if (_startupError is not null)
        {
            throw new InvalidOperationException(
                "The Altim tray windows could not be created ("
                    + ExceptionSummary.Describe(_startupError)
                    + ").",
                _startupError);
        }
    }

    /// <summary>
    /// Raised for every message either window receives that this class does not
    /// consume itself. Handlers run on the message loop thread and must return
    /// promptly: blocking here blocks the tray.
    /// </summary>
    public event Action<WindowMessage>? MessageReceived;

    /// <summary>The message-only window that owns the notification icon.</summary>
    public IntPtr IconWindow => _iconWindow;

    /// <summary>The hidden top-level window that receives broadcasts and owns menus.</summary>
    public IntPtr ShellWindow => _shellWindow;

    /// <summary>The registered <c>TaskbarCreated</c> broadcast message identifier.</summary>
    public uint TaskbarCreatedMessage => _taskbarCreated;

    /// <summary>True when the caller is already on the message loop thread.</summary>
    public bool IsOnWindowThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>
    /// Runs <paramref name="action"/> on the message loop thread. Completes
    /// synchronously when the caller is already there, which is the common case for
    /// work raised from a window message.
    /// </summary>
    /// <param name="action">The work to run.</param>
    /// <param name="ct">Cancels the call before the work is queued.</param>
    /// <returns>A task that completes when the work has run.</returns>
    public ValueTask InvokeAsync(Action action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        if (IsOnWindowThread)
        {
            action();
            return ValueTask.CompletedTask;
        }

        var item = new WorkItem(action);
        _work.Enqueue(item);

        if (!NativeMethods.PostMessageW(_iconWindow, WmWorkItem, IntPtr.Zero, IntPtr.Zero))
        {
            // The loop is gone; nothing will ever drain the queue.
            Drain(cancel: true);
        }

        return new ValueTask(item.Completion.Task);
    }

    /// <summary>
    /// Queues <paramref name="action"/> on the message loop thread without waiting for
    /// it. Used from operating system callbacks, which must not block.
    /// </summary>
    /// <param name="action">The work to run.</param>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_disposed)
        {
            return;
        }

        _work.Enqueue(new WorkItem(action));
        _ = NativeMethods.PostMessageW(_iconWindow, WmWorkItem, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Destroys both windows, ends the message loop and joins the thread. Safe to call
    /// more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_thread.IsAlive)
        {
            _ = NativeMethods.PostMessageW(_iconWindow, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                // The loop is wedged, most likely inside a modal menu. Leave the thread
                // to the process teardown rather than aborting it.
                return;
            }
        }

        Drain(cancel: true);
        _ready.Dispose();
    }

    private void ThreadMain()
    {
        try
        {
            CreateWindows();
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        MSG msg;
        while (true)
        {
            int result = NativeMethods.GetMessageW(&msg, IntPtr.Zero, 0, 0);
            if (result is 0 or (-1))
            {
                break;
            }

            _ = NativeMethods.TranslateMessage(&msg);
            _ = NativeMethods.DispatchMessageW(&msg);
        }

        Teardown();
    }

    private void CreateWindows()
    {
        _moduleHandle = NativeMethods.GetModuleHandleW(null);
        _self = GCHandle.Alloc(this, GCHandleType.Normal);

        fixed (char* className = _className)
        fixed (char* taskbarCreated = "TaskbarCreated")
        {
            var wc = default(WNDCLASSEXW);
            wc.cbSize = (uint)sizeof(WNDCLASSEXW);
            wc.lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc;
            wc.hInstance = _moduleHandle;
            wc.lpszClassName = className;

            _atom = NativeMethods.RegisterClassExW(&wc);
            if (_atom == 0)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"RegisterClassEx failed for the Altim tray window class (Win32 error {Marshal.GetLastWin32Error()})."));
            }

            // Message-only: owns the notification icon, invisible to enumeration.
            _iconWindow = NativeMethods.CreateWindowExW(
                0, className, null, 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, _moduleHandle, IntPtr.Zero);

            if (_iconWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"CreateWindowEx failed for the Altim message-only window (Win32 error {Marshal.GetLastWin32Error()})."));
            }

            // Hidden top-level: receives broadcasts, owns popup menus. Never shown, and
            // WS_EX_TOOLWINDOW keeps it out of Alt+Tab even if something shows it.
            _shellWindow = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
                className, null, NativeMethods.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, _moduleHandle, IntPtr.Zero);

            if (_shellWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"CreateWindowEx failed for the Altim broadcast window (Win32 error {Marshal.GetLastWin32Error()})."));
            }

            _taskbarCreated = NativeMethods.RegisterWindowMessageW(taskbarCreated);
        }

        IntPtr cookie = GCHandle.ToIntPtr(_self);
        NativeMethods.SetWindowPointer(_iconWindow, NativeMethods.GWLP_USERDATA, cookie);
        NativeMethods.SetWindowPointer(_shellWindow, NativeMethods.GWLP_USERDATA, cookie);
    }

    private void Teardown()
    {
        if (_shellWindow != IntPtr.Zero)
        {
            NativeMethods.SetWindowPointer(_shellWindow, NativeMethods.GWLP_USERDATA, IntPtr.Zero);
            _ = NativeMethods.DestroyWindow(_shellWindow);
            _shellWindow = IntPtr.Zero;
        }

        if (_iconWindow != IntPtr.Zero)
        {
            NativeMethods.SetWindowPointer(_iconWindow, NativeMethods.GWLP_USERDATA, IntPtr.Zero);
            _ = NativeMethods.DestroyWindow(_iconWindow);
            _iconWindow = IntPtr.Zero;
        }

        if (_atom != 0)
        {
            fixed (char* className = _className)
            {
                _ = NativeMethods.UnregisterClassW(className, _moduleHandle);
            }

            _atom = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        Drain(cancel: true);
    }

    private void Drain(bool cancel)
    {
        while (_work.TryDequeue(out WorkItem? item))
        {
            if (cancel)
            {
                item.Completion.TrySetCanceled();
                continue;
            }

            try
            {
                item.Action();
                item.Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
            }
        }
    }

    private IntPtr Dispatch(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmWorkItem)
        {
            Drain(cancel: false);
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_CLOSE && hWnd == _iconWindow)
        {
            // Raised so owners can remove the icon while the window still exists.
            MessageReceived?.Invoke(new WindowMessage(hWnd, msg, wParam, lParam));
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        MessageReceived?.Invoke(new WindowMessage(hWnd, msg, wParam, lParam));
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            IntPtr cookie = NativeMethods.GetWindowPointer(hWnd, NativeMethods.GWLP_USERDATA);
            if (cookie != IntPtr.Zero && GCHandle.FromIntPtr(cookie).Target is TrayWindow window)
            {
                return window.Dispatch(hWnd, msg, wParam, lParam);
            }
        }
        catch (Exception)
        {
            // A managed exception must never unwind into the window procedure: it would
            // tear down the process from inside the shell's call stack.
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private sealed class WorkItem(Action action)
    {
        public Action Action { get; } = action;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>One window message, as delivered to the tray message loop.</summary>
/// <param name="Window">The window that received it.</param>
/// <param name="Message">The message identifier.</param>
/// <param name="WParam">First parameter.</param>
/// <param name="LParam">Second parameter.</param>
internal readonly record struct WindowMessage(IntPtr Window, uint Message, IntPtr WParam, IntPtr LParam);

#endif
