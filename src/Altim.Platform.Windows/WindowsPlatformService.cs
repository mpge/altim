#if WINDOWS

using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.Windows.Interop;
using Microsoft.Win32;

namespace Altim.Platform.Windows;

/// <summary>
/// The Windows implementation of <see cref="IPlatformService"/>: the tray host, the
/// tray anchor, and the two system signals the rest of the application reacts to.
/// </summary>
/// <remarks>
/// <para>
/// Resume is deliberately taken from two sources. <see cref="WindowsPowerEvents"/>
/// carries the classic <c>PBT_APMRESUMEAUTOMATIC</c> broadcast, which is what a
/// machine using S3 sleep sends. A modern standby machine can stay in S0 and never
/// send it, so the service also registers for the <c>GUID_CONSOLE_DISPLAY_STATE</c>
/// power setting on the tray's top-level window and treats the off-to-on transition
/// as a wake. Signals arriving close together are coalesced, so a wake that produces
/// both raises <see cref="SystemResumed"/> once.
/// </para>
/// <para>
/// <b>Suspend is taken from one source only, and the asymmetry is the point.</b>
/// <see cref="SystemSuspending"/> comes from <c>PBT_APMSUSPEND</c> and nothing else. The
/// display switching <em>off</em> is deliberately not treated as the machine going to
/// sleep, even though the display switching <em>on</em> is treated as a wake: a monitor
/// that has blanked after an idle timeout is not a machine that has stopped, and pausing
/// the scheduler there would stop recording an agent that is working away against a dark
/// screen. An extra wake costs one refresh; a wrong suspend costs the history.
/// </para>
/// <para>
/// The consequence is that a modern standby machine can report a resume with no suspend
/// before it. <see cref="IPlatformService.SystemSuspending"/> documents that as allowed,
/// and the composition root handles it.
/// </para>
/// <para>
/// <see cref="SystemEvents"/> is a static event source that keeps its subscribers
/// alive for the life of the process. Every handler attached here is detached in
/// <see cref="Dispose"/>; without that, a platform service created per test or per
/// restart leaks itself and its tray host.
/// </para>
/// </remarks>
public sealed class WindowsPlatformService : IPlatformService, IDisposable
{
    /// <summary>
    /// Two wake signals inside this window are the same wake. Long enough to absorb
    /// the gap between the power broadcast and the display coming back, short enough
    /// that a genuine second wake is never swallowed.
    /// </summary>
    private static readonly TimeSpan ResumeCoalescingWindow = TimeSpan.FromSeconds(5);

    private readonly WindowsTrayHost _tray;
    private readonly WindowsPowerEvents _power;
    private readonly bool _ownsTray;

    private IntPtr _displayStateRegistration;
    private long _lastResumeTicks;
    private long _lastSuspendTicks;
    private bool _displayWasOff;
    private bool _taskbarDark;
    private bool _appDark;
    private bool _disposed;

    /// <summary>
    /// Creates the platform service and its tray host.
    /// </summary>
    /// <param name="tooltip">Initial tray tooltip.</param>
    /// <param name="iconDirectory">
    /// Directory holding the icon assets, or <see langword="null"/> to discover it.
    /// </param>
    public WindowsPlatformService(string tooltip = "Altim", string? iconDirectory = null)
        : this(new WindowsTrayHost(tooltip, iconDirectory), ownsTray: true)
    {
    }

    /// <summary>
    /// Creates the platform service over an existing tray host.
    /// </summary>
    /// <param name="tray">The tray host to expose.</param>
    /// <param name="ownsTray">
    /// True when disposing this service should also dispose <paramref name="tray"/>.
    /// <see cref="IPlatformService"/> says the service owns the tray, so this is only
    /// false when a test wants to keep the host alive across services.
    /// </param>
    public WindowsPlatformService(WindowsTrayHost tray, bool ownsTray = true)
    {
        ArgumentNullException.ThrowIfNull(tray);

        _tray = tray;
        _ownsTray = ownsTray;
        _power = new WindowsPowerEvents();
        _power.SystemResumed += OnPowerResume;
        _power.SystemSuspending += OnPowerSuspend;

        _taskbarDark = WindowsTheme.TaskbarIsDark();
        _appDark = WindowsTheme.AppIsDark();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _tray.Window.MessageReceived += OnWindowMessage;
        RegisterForDisplayState();
    }

    /// <inheritdoc />
    public event EventHandler? SystemSuspending;

    /// <inheritdoc />
    public event EventHandler? SystemResumed;

    /// <inheritdoc />
    public event EventHandler? ThemeChanged;

    /// <inheritdoc />
    public ITrayHost Tray => _tray;

    /// <summary>True when Windows is currently asking for a dark application theme.</summary>
    public bool IsDarkTheme => _appDark;

    /// <inheritdoc />
    /// <remarks>
    /// The query is marshalled to the thread that owns the icon, and returns
    /// <see langword="null"/> when the shell refuses it — the case when the icon sits
    /// in the Windows 11 overflow flyout.
    /// </remarks>
    public async ValueTask<PixelRect?> GetTrayAnchorAsync()
    {
        if (_disposed)
        {
            return null;
        }

        PixelRect? anchor = null;
        await _tray.Window.InvokeAsync(() => anchor = _tray.ResolveAnchor(null)).ConfigureAwait(false);
        return anchor;
    }

    /// <summary>
    /// Detaches every handler, including the static ones, and disposes the tray host.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _power.SystemResumed -= OnPowerResume;
        _power.SystemSuspending -= OnPowerSuspend;
        _power.Dispose();

        UnregisterForDisplayState();
        _tray.Window.MessageReceived -= OnWindowMessage;

        if (_ownsTray)
        {
            _tray.Dispose();
        }
    }

    private unsafe void RegisterForDisplayState()
    {
        _tray.Window.Post(() =>
        {
            Guid setting = NativeMethods.GUID_CONSOLE_DISPLAY_STATE;
            _displayStateRegistration = NativeMethods.RegisterPowerSettingNotification(
                _tray.Window.ShellWindow, &setting, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
        });
    }

    private void UnregisterForDisplayState()
    {
        IntPtr registration = _displayStateRegistration;
        if (registration == IntPtr.Zero)
        {
            return;
        }

        _displayStateRegistration = IntPtr.Zero;
        _ = NativeMethods.UnregisterPowerSettingNotification(registration);
    }

    private unsafe void OnWindowMessage(WindowMessage message)
    {
        if (message.Message != NativeMethods.WM_POWERBROADCAST)
        {
            return;
        }

        int subtype = (int)message.WParam;
        if (subtype is NativeMethods.PBT_APMRESUMEAUTOMATIC or NativeMethods.PBT_APMRESUMESUSPEND)
        {
            RaiseResume();
            return;
        }

        if (subtype == NativeMethods.PBT_APMSUSPEND)
        {
            RaiseSuspend();
            return;
        }

        if (subtype != NativeMethods.PBT_POWERSETTINGCHANGE || message.LParam == IntPtr.Zero)
        {
            return;
        }

        var setting = (POWERBROADCAST_SETTING*)message.LParam;
        if (setting->PowerSetting != NativeMethods.GUID_CONSOLE_DISPLAY_STATE || setting->DataLength < 1)
        {
            return;
        }

        // 0 off, 1 on, 2 dimmed. On a modern standby machine the display going from
        // off to on is the observable end of the sleep.
        byte state = setting->Data[0];
        if (state == 0)
        {
            _displayWasOff = true;
        }
        else if (state == 1 && _displayWasOff)
        {
            _displayWasOff = false;
            RaiseResume();
        }
    }

    private void OnPowerResume(object? sender, EventArgs e) => RaiseResume();

    private void OnPowerSuspend(object? sender, EventArgs e) => RaiseSuspend();

    private void RaiseResume()
    {
        if (_disposed)
        {
            return;
        }

        long now = Environment.TickCount64;
        long previous = Interlocked.Exchange(ref _lastResumeTicks, now);
        if (previous != 0 && now - previous < (long)ResumeCoalescingWindow.TotalMilliseconds)
        {
            return;
        }

        SystemResumed?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSuspend()
    {
        if (_disposed)
        {
            return;
        }

        // The same coalescing rule as the wake: SystemEvents and the window's own broadcast
        // both carry PBT_APMSUSPEND, so one sleep arrives twice.
        long now = Environment.TickCount64;
        long previous = Interlocked.Exchange(ref _lastSuspendTicks, now);
        if (previous != 0 && now - previous < (long)ResumeCoalescingWindow.TotalMilliseconds)
        {
            return;
        }

        SystemSuspending?.Invoke(this, EventArgs.Empty);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        bool taskbarDark = WindowsTheme.TaskbarIsDark();
        bool appDark = WindowsTheme.AppIsDark();
        if (taskbarDark == _taskbarDark && appDark == _appDark)
        {
            return;
        }

        _taskbarDark = taskbarDark;
        _appDark = appDark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}

#endif
