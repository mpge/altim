using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.MacOS.Interop;

namespace Altim.Platform.MacOS;

/// <summary>
/// The macOS implementation of <see cref="IPlatformService"/>: the menu bar host, the
/// status item's rectangle, and the two system signals the rest of Altim reacts to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> No part of this has run on macOS. Every observation registered below
/// is written from Apple's documentation, and each one degrades to "that signal never
/// fires" rather than to a failure.
/// </para>
/// <para>
/// <b>Sleep and wake come from <c>NSWorkspace</c>'s own notification centre.</b> This is
/// the trap the class exists to avoid: <c>NSWorkspaceDidWakeNotification</c> is posted to
/// <c>NSWorkspace.shared.notificationCenter</c>, not to
/// <c>NSNotificationCenter.defaultCenter</c>. Observing the default centre is accepted
/// silently and then receives nothing at all, which looks exactly like a machine that never
/// sleeps. Both <c>DidWake</c> and <c>ScreensDidWake</c> are observed, because a machine
/// that only slept its displays posts the second and not the first, and the two are
/// coalesced so one wake raises <see cref="SystemResumed"/> once.
/// </para>
/// <para>
/// <b><c>WillSleep</c> is the counterpart, and only <c>WillSleep</c>.</b>
/// <c>NSWorkspaceWillSleepNotification</c> is posted from the same workspace centre before
/// the machine suspends, and it raises <see cref="SystemSuspending"/>.
/// <c>NSWorkspaceScreensDidSleepNotification</c> is deliberately <em>not</em> observed even
/// though its waking twin is: a display that has gone dark is not a machine that has
/// stopped, and pausing the scheduler there would stop recording an agent still working.
/// The asymmetry is the same one the Windows service makes, for the same reason.
/// </para>
/// <para>
/// <b>Appearance comes from a third centre, and that is deliberate.</b> macOS posts
/// <c>AppleInterfaceThemeChangedNotification</c> to
/// <c>NSDistributedNotificationCenter.defaultCenter</c> — it is a system-wide broadcast, not
/// a workspace event, and it is not carried on <c>NSWorkspace</c>'s centre. Observing the
/// workspace centre for it would hit the same silent-nothing failure as above, so it is
/// registered where the notification is actually posted. The current value is read from
/// <c>NSUserDefaults</c>' <c>AppleInterfaceStyle</c>, which is the string <c>Dark</c> in dark
/// mode and absent otherwise.
/// </para>
/// <para>
/// <b>That notification name is undocumented, and this is the one place in the macOS layer
/// that depends on something Apple does not publish.</b> <c>AppleInterfaceThemeChangedNotification</c>
/// appears in no public SDK header and has no documentation page; it is used this way by
/// Flutter, Fyne and every other cross-platform toolkit that needs the signal, which is
/// corroboration and not a guarantee. Apple can retire it without notice. What that would cost
/// is bounded: <see cref="ThemeChanged"/> would stop firing and Altim would keep whatever
/// appearance it read at start-up, which is a stale theme rather than a failure. Everything
/// else on this class is documented API — the three <c>NSWorkspace</c> names are declared in
/// <c>NSWorkspace.h</c>, and Apple states in terms that registering them anywhere but
/// <c>NSWorkspace</c>'s own centre receives nothing.
/// </para>
/// <para>
/// <b>The tray icon is not swapped on a theme change.</b> It is a template image; the menu
/// bar tints it. <see cref="ThemeChanged"/> exists for the application's own light and dark
/// surfaces.
/// </para>
/// <para>
/// <b>Accessory app.</b> Altim runs with no Dock tile, which also means it has no
/// application menu bar of its own. Nothing here assumes one exists.
/// </para>
/// </remarks>
public sealed class MacOSPlatformService : IPlatformService, IObjCCallbackSink, IDisposable
{
    /// <summary>
    /// Two wake signals inside this window are the same wake — the same rule, and the same
    /// duration, the Windows service uses.
    /// </summary>
    private static readonly TimeSpan ResumeCoalescingWindow = TimeSpan.FromSeconds(5);

    /// <summary>Posted by <c>NSWorkspace</c> when the machine wakes from sleep.</summary>
    private const string DidWakeNotification = "NSWorkspaceDidWakeNotification";

    /// <summary>Posted by <c>NSWorkspace</c> just before the machine suspends.</summary>
    private const string WillSleepNotification = "NSWorkspaceWillSleepNotification";

    /// <summary>Posted by <c>NSWorkspace</c> when the displays wake, which S0 sleep may be all of.</summary>
    private const string ScreensDidWakeNotification = "NSWorkspaceScreensDidWakeNotification";

    /// <summary>
    /// The system-wide appearance broadcast, posted to the distributed centre. Undocumented:
    /// see the class remarks for what depends on it and what its disappearance would cost.
    /// </summary>
    private const string InterfaceThemeChangedNotification = "AppleInterfaceThemeChangedNotification";

    private readonly MacOSTrayHost _tray;
    private readonly ObjCCallbackTarget? _observer;
    private readonly bool _ownsTray;

    private long _lastResumeTicks;
    private long _lastSuspendTicks;
    private bool _dark;
    private bool _disposed;

    /// <summary>Creates the platform service and its menu bar host.</summary>
    /// <param name="tooltip">Initial menu bar tooltip.</param>
    /// <param name="assetDirectory">
    /// Directory holding the template icon assets, or <see langword="null"/> to discover it.
    /// </param>
    public MacOSPlatformService(string tooltip = "Altim", string? assetDirectory = null)
        : this(new MacOSTrayHost(tooltip, assetDirectory), ownsTray: true)
    {
    }

    /// <summary>Creates the platform service over an existing menu bar host.</summary>
    /// <param name="tray">The tray host to expose.</param>
    /// <param name="ownsTray">
    /// True when disposing this service should also dispose <paramref name="tray"/>.
    /// <see cref="IPlatformService"/> says the service owns the tray, so this is only false
    /// when a test wants the host to outlive the service.
    /// </param>
    public MacOSPlatformService(MacOSTrayHost tray, bool ownsTray = true)
    {
        ArgumentNullException.ThrowIfNull(tray);

        _tray = tray;
        _ownsTray = ownsTray;

        // A second callback object, not the tray's: notifications have to land on this
        // service's sink, and one Objective-C instance can only route to one of them.
        _observer = OperatingSystem.IsMacOS() ? ObjCCallbackTarget.TryCreate(this) : null;

        _dark = ReadDarkAppearance();
        RegisterObservers();
    }

    /// <inheritdoc />
    public event EventHandler? SystemSuspending;

    /// <inheritdoc />
    public event EventHandler? SystemResumed;

    /// <inheritdoc />
    public event EventHandler? ThemeChanged;

    /// <inheritdoc />
    public ITrayHost Tray => _tray;

    /// <summary>True when macOS is currently asking for a dark appearance.</summary>
    public bool IsDarkTheme => _dark;

    /// <summary>
    /// True when the Objective-C runtime was reachable. False means Altim runs with no menu
    /// bar and no system signals, which is reported at start-up rather than thrown.
    /// </summary>
    public bool IsSupported => _observer is not null && _tray.IsSupported;

    /// <inheritdoc />
    /// <remarks>
    /// Marshalled to the Cocoa main thread, because the status item's window may not be
    /// read from anywhere else. Null when there is no status item, when it has not been
    /// placed yet, or when macOS has hidden it because the menu bar ran out of room.
    /// <b>Unverified</b>, including the assumption that the anchor is wanted in physical
    /// pixels — see <see cref="MacOSCoordinates"/>.
    /// </remarks>
    public async ValueTask<PixelRect?> GetTrayAnchorAsync()
    {
        if (_disposed)
        {
            return null;
        }

        ObjCCallbackTarget? target = _tray.Target;
        if (target is null)
        {
            return null;
        }

        PixelRect? anchor = null;
        await target.InvokeAsync(() => anchor = _tray.GetAnchor()).ConfigureAwait(false);
        return anchor;
    }

    /// <summary>Removes every observation and disposes the menu bar host.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        RemoveObservers();
        _observer?.Dispose();

        if (_ownsTray)
        {
            _tray.Dispose();
        }
    }

    void IObjCCallbackSink.OnStatusItemClicked(IntPtr sender)
    {
        // The tray host owns the status item's action; this sink only sees notifications.
    }

    void IObjCCallbackSink.OnMenuItemClicked(IntPtr sender)
    {
        // As above.
    }

    void IObjCCallbackSink.OnNotification(IntPtr notification)
    {
        if (_disposed || notification == IntPtr.Zero)
        {
            return;
        }

        string? name = ObjC.ReadString(ObjC.Send(notification, ObjC.Selector("name")));
        switch (name)
        {
            case DidWakeNotification:
            case ScreensDidWakeNotification:
                RaiseResume();
                break;

            case WillSleepNotification:
                RaiseSuspend();
                break;

            case InterfaceThemeChangedNotification:
                RaiseThemeChanged();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Reads the system appearance.
    /// </summary>
    /// <returns>
    /// True for dark. <c>AppleInterfaceStyle</c> is absent in light mode rather than set to
    /// a value, so a missing key means light — which is also the answer on a host with no
    /// Objective-C runtime.
    /// </returns>
    private static bool ReadDarkAppearance()
    {
        IntPtr defaultsClass = ObjC.Class("NSUserDefaults");
        if (defaultsClass == IntPtr.Zero)
        {
            return false;
        }

        IntPtr defaults = ObjC.Send(defaultsClass, ObjC.Selector("standardUserDefaults"));
        if (defaults == IntPtr.Zero)
        {
            return false;
        }

        IntPtr key = ObjC.CreateString("AppleInterfaceStyle");
        try
        {
            IntPtr value = ObjC.Send(defaults, ObjC.Selector("stringForKey:"), key);
            string? style = ObjC.ReadString(value);
            return style is not null && style.StartsWith("Dark", StringComparison.Ordinal);
        }
        finally
        {
            ObjC.Release(key);
        }
    }

    private void RegisterObservers()
    {
        ObjCCallbackTarget? observer = _observer;
        if (observer is null)
        {
            return;
        }

        observer.Post(() =>
        {
            IntPtr workspaceCentre = WorkspaceNotificationCentre();
            AddObserver(workspaceCentre, DidWakeNotification);
            AddObserver(workspaceCentre, ScreensDidWakeNotification);
            AddObserver(workspaceCentre, WillSleepNotification);

            AddObserver(DistributedNotificationCentre(), InterfaceThemeChangedNotification);
        });
    }

    private void RemoveObservers()
    {
        ObjCCallbackTarget? observer = _observer;
        if (observer is null)
        {
            return;
        }

        IntPtr handle = observer.Handle;

        // Synchronous: the observer object must be unregistered before it is released, or a
        // later notification reaches a freed object.
        try
        {
            observer.InvokeAsync(() =>
            {
                RemoveObserver(WorkspaceNotificationCentre(), handle);
                RemoveObserver(DistributedNotificationCentre(), handle);
            }).AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The main run loop has already stopped; the registrations die with it.
        }
    }

    private static IntPtr WorkspaceNotificationCentre()
    {
        IntPtr workspaceClass = ObjC.Class("NSWorkspace");
        if (workspaceClass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr workspace = ObjC.Send(workspaceClass, ObjC.Selector("sharedWorkspace"));
        return workspace == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(workspace, ObjC.Selector("notificationCenter"));
    }

    private static IntPtr DistributedNotificationCentre()
    {
        IntPtr centreClass = ObjC.Class("NSDistributedNotificationCenter");
        return centreClass == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(centreClass, ObjC.Selector("defaultCenter"));
    }

    private void AddObserver(IntPtr centre, string notificationName)
    {
        ObjCCallbackTarget? observer = _observer;
        if (centre == IntPtr.Zero || observer is null)
        {
            return;
        }

        IntPtr name = ObjC.CreateString(notificationName);
        try
        {
            ObjC.SendAddObserver(
                centre,
                ObjC.Selector("addObserver:selector:name:object:"),
                observer.Handle,
                ObjCCallbackTarget.NotificationSelector,
                name,
                IntPtr.Zero);
        }
        finally
        {
            ObjC.Release(name);
        }
    }

    private static void RemoveObserver(IntPtr centre, IntPtr observer)
    {
        if (centre != IntPtr.Zero && observer != IntPtr.Zero)
        {
            ObjC.Send(centre, ObjC.Selector("removeObserver:"), observer);
        }
    }

    private void RaiseResume()
    {
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
        long now = Environment.TickCount64;
        long previous = Interlocked.Exchange(ref _lastSuspendTicks, now);
        if (previous != 0 && now - previous < (long)ResumeCoalescingWindow.TotalMilliseconds)
        {
            return;
        }

        SystemSuspending?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseThemeChanged()
    {
        bool dark = ReadDarkAppearance();
        if (dark == _dark)
        {
            return;
        }

        _dark = dark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}
