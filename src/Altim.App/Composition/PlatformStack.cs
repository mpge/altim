using Altim.App.Diagnostics;
using Altim.App.Services;
using Altim.Core.Abstractions;
using Altim.Platform.Linux;
using Altim.Platform.MacOS;
#if WINDOWS
using Altim.Platform.Windows;
#endif

namespace Altim.App.Composition;

/// <summary>
/// The native half of the application, resolved once at start-up.
/// </summary>
/// <remarks>
/// <para>
/// Every member of this stack can be absent. A machine with no tray still runs Altim; so
/// does one whose notification platform refuses registration, which is the first risk in
/// ARCHITECTURE.md and the one most likely to bite a real installation. Absence is recorded
/// in the <see cref="StartupReport"/> rather than thrown, and the rest of the application
/// binds to the interfaces either way.
/// </para>
/// <para>
/// Only the Windows branch is chosen inside <c>#if WINDOWS</c>, because the Windows types
/// exist exclusively in <c>Altim.Platform.Windows</c>' Windows-flavoured target framework
/// and a plain <c>net10.0</c> composition root binds to a facade that contains no types at
/// all. <c>Altim.Platform.MacOS</c> and <c>Altim.Platform.Linux</c> are plain
/// <c>net10.0</c> assemblies — see the reasoning in their project files — so they are
/// compiled into every build, including this one, and are selected by
/// <see cref="OperatingSystem.IsMacOS"/> and <see cref="OperatingSystem.IsLinux"/> alone.
/// Each of their types is inert off its own platform, so constructing one here on the wrong
/// host is safe; the run-time check is what stops it happening.
/// </para>
/// </remarks>
internal sealed class PlatformStack : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private bool _disposed;

    private PlatformStack(INotificationService notifications, IAutoStartService autoStart)
    {
        Notifications = notifications;
        AutoStart = autoStart;
    }

    /// <summary>The tray host and system signals, or null when this platform has none.</summary>
    public IPlatformService? Platform { get; private init; }

    /// <summary>Never null: a platform without notifications gets one that drops them.</summary>
    public INotificationService Notifications { get; }

    /// <summary>Never null: a platform without autostart gets one that reports false.</summary>
    public IAutoStartService AutoStart { get; }

    /// <summary>
    /// The running-process detector providers use to say whether an agent is active, or null
    /// to let each provider build its own.
    /// </summary>
    public IProcessMonitor? Processes { get; private init; }

    /// <summary>True when notifications will actually reach the user.</summary>
    /// <remarks>
    /// Known at start-up on Windows and macOS, both of which either hold a registration or
    /// do not. On Linux it is an assumption: the notification service connects to the
    /// session bus on its first message, so this reports true and the first failed delivery
    /// adds the degraded condition instead.
    /// </remarks>
    public bool NotificationsWork { get; private init; }

    /// <summary>Builds the stack for this machine.</summary>
    /// <param name="report">Collects anything that had to be degraded.</param>
    public static PlatformStack Create(StartupReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return CreateWindows(report);
        }
#endif

        if (OperatingSystem.IsMacOS())
        {
            return CreateMacOS(report);
        }

        if (OperatingSystem.IsLinux())
        {
            return CreateLinux(report);
        }

        report.Add("No tray support on this platform yet");
        AltimLog.Write("platform", "No platform implementation for this OS; running without a tray.");
        return new PlatformStack(new NullNotificationService(), new NullAutoStartService());
    }

    /// <summary>Releases the tray host, the notification registration and the power hooks.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Reverse order: the notification registration is torn down before the tray host
        // that outlived it, and the tray host removes its icon while its window is alive,
        // which is what stops a ghost icon being left behind.
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                _owned[i].Dispose();
            }
            catch (Exception ex)
            {
                AltimLog.Write("platform", "Disposing a platform service failed", ex);
            }
        }

        _owned.Clear();
    }

    private static PlatformStack CreateMacOS(StartupReport report)
    {
        MacOSPlatformService? platform = null;
        try
        {
            platform = new MacOSPlatformService("Altim");
            if (!platform.IsSupported)
            {
                // The Objective-C runtime was not reachable, or the status item could not be
                // created. The service still answers every call; it simply never signals.
                report.Add("Menu bar icon unavailable; Altim is running without one");
                AltimLog.Write("tray", "The macOS menu bar host reported itself unsupported.");
            }
        }
        catch (Exception ex)
        {
            report.Add("Menu bar icon unavailable; Altim is running without one");
            AltimLog.Write("tray", "Creating the macOS menu bar host failed", ex);
        }

        INotificationService notifications = new NullNotificationService();
        bool notificationsWork = false;
        MacOSNotificationService? centre = null;
        try
        {
            centre = new MacOSNotificationService();
            notifications = centre;
            notificationsWork = centre.IsAvailable;

            if (!centre.IsAvailable)
            {
                // The reason is already a sentence written for a user — most often that this
                // is not a bundled build, which is the one macOS turns into a process-killing
                // Objective-C exception rather than an error code.
                report.Add(centre.UnavailableReason ?? "Notifications unavailable; usage alerts are off");
                AltimLog.Write(
                    "notifications",
                    "UNUserNotificationCenter is unavailable: " + (centre.UnavailableReason ?? "no reason reported"));
            }
        }
        catch (Exception ex)
        {
            report.Add("Notifications unavailable; usage alerts are off");
            AltimLog.Write("notifications", "The macOS notification centre could not be reached", ex);
        }

        IAutoStartService autoStart;
        try
        {
            var loginItem = new MacOSAutoStartService();
            autoStart = loginItem;

            if (!loginItem.IsSupported)
            {
                report.Add(loginItem.UnavailableReason ?? "Start at login is unavailable");
                AltimLog.Write(
                    "autostart",
                    "SMAppService is unavailable: " + (loginItem.UnavailableReason ?? "no reason reported"));
            }
        }
        catch (Exception ex)
        {
            autoStart = new NullAutoStartService();
            report.Add("Start at login is unavailable");
            AltimLog.Write("autostart", "Creating the macOS login item service failed", ex);
        }

        IProcessMonitor? processes = null;
        try
        {
            processes = new MacOSProcessMonitor();
        }
        catch (Exception ex)
        {
            AltimLog.Write("processes", "Creating the process monitor failed; providers will build their own", ex);
        }

        var stack = new PlatformStack(notifications, autoStart)
        {
            Platform = platform,
            Processes = processes,
            NotificationsWork = notificationsWork,
        };

        // Neither the notification centre nor the login item service holds anything to tear
        // down: one retains a singleton it did not create, the other a class object. Only the
        // platform service owns state — the observer registrations and the status item.
        if (platform is not null)
        {
            stack._owned.Add(platform);
        }

        return stack;
    }

    private static PlatformStack CreateLinux(StartupReport report)
    {
        LinuxPlatformService? platform = null;
        LinuxTrayHost? tray = null;
        try
        {
            tray = new LinuxTrayHost("Altim");
            platform = new LinuxPlatformService(tray);
        }
        catch (Exception ex)
        {
            tray?.Dispose();
            tray = null;
            report.Add("Panel icon unavailable; Altim is running without one");
            AltimLog.Write("tray", "Creating the Linux panel host failed", ex);
        }

        // The panel presence is published by ShowAsync, so a desktop with no
        // StatusNotifierItem host is not known about yet. LinuxTrayHost records the reason
        // when it finds out, and AltimRuntime already reports an icon that did not appear.
        INotificationService notifications = new NullNotificationService();

        // True by assumption. LinuxNotificationService opens its session-bus connection on
        // the first message it is asked to deliver, so there is nothing to probe at
        // start-up and probing on purpose would cost a bus round trip inside the 800ms
        // budget. The first failure reports itself instead, through DeliveryFailed.
        bool notificationsWork = true;
        LinuxNotificationService? daemon = null;
        try
        {
            daemon = new LinuxNotificationService(
                "Altim", LinuxAutoStartService.DefaultEntryName, appIcon: null);
            notifications = daemon;
            daemon.DeliveryFailed += (_, failure) =>
            {
                report.Add("Notifications are not reaching the desktop; usage alerts may be missed");
                AltimLog.Write(
                    "notifications",
                    "A notification was not delivered (" + failure.Delivery + "): " + failure.Reason);
            };
        }
        catch (Exception ex)
        {
            notificationsWork = false;
            report.Add("Notifications unavailable; usage alerts are off");
            AltimLog.Write("notifications", "Creating the Linux notification service failed", ex);
        }

        IAutoStartService autoStart;
        try
        {
            var entry = new LinuxAutoStartService();
            autoStart = entry;

            if (!entry.IsSupported)
            {
                report.Add("Start at login is unavailable");
                AltimLog.Write(
                    "autostart",
                    "No autostart directory or no executable path; the desktop entry cannot be written.");
            }
        }
        catch (Exception ex)
        {
            autoStart = new NullAutoStartService();
            report.Add("Start at login is unavailable");
            AltimLog.Write("autostart", "Creating the XDG autostart service failed", ex);
        }

        IProcessMonitor? processes = null;
        try
        {
            processes = new LinuxProcessMonitor();
        }
        catch (Exception ex)
        {
            AltimLog.Write("processes", "Creating the process monitor failed; providers will build their own", ex);
        }

        var stack = new PlatformStack(notifications, autoStart)
        {
            Platform = platform,
            Processes = processes,
            NotificationsWork = notificationsWork,
        };

        // The notification service owns a session-bus connection; the platform service owns
        // two more plus its subscriptions and the panel host. Disposed in reverse, so the
        // notification connection closes before the one carrying the panel item.
        if (daemon is not null)
        {
            stack._owned.Add(daemon);
        }

        if (platform is not null)
        {
            stack._owned.Add(platform);
        }
        else if (tray is not null)
        {
            stack._owned.Add(tray);
        }

        return stack;
    }

#if WINDOWS
    private static PlatformStack CreateWindows(StartupReport report)
    {
        WindowsPlatformService? platform = null;
        try
        {
            platform = new WindowsPlatformService("Altim");
        }
        catch (Exception ex)
        {
            report.Add("Tray icon unavailable; Altim is running without one");
            AltimLog.Write("tray", "Creating the Windows tray host failed", ex);
        }

        INotificationService notifications = new NullNotificationService();
        bool notificationsWork = false;
        WindowsNotificationService? toasts = null;
        try
        {
            toasts = new WindowsNotificationService();
            notifications = toasts;
            notificationsWork = toasts.IsRegistered;

            if (toasts.IsSuppressedByElevation)
            {
                report.Add("Notifications are suppressed because Altim is running elevated");
                AltimLog.Write("notifications", "Elevated process; Windows discards its toasts.");
            }
            else if (!toasts.IsRegistered)
            {
                report.Add("Notifications unavailable; usage alerts are off");
                AltimLog.Write(
                    "notifications",
                    "AppNotificationManager.Register failed: " + (toasts.RegistrationError ?? "no reason reported"));
            }
        }
        catch (Exception ex)
        {
            // The Windows App Runtime is a deployment dependency, and a machine without it
            // throws from the projection rather than returning a failure. Altim runs and
            // does not notify; it never fails to start.
            report.Add("Notifications unavailable; usage alerts are off");
            AltimLog.Write("notifications", "The notification platform could not be reached", ex);
        }

        IAutoStartService autoStart;
        try
        {
            autoStart = new WindowsAutoStartService();
        }
        catch (Exception ex)
        {
            autoStart = new NullAutoStartService();
            report.Add("Start with Windows is unavailable");
            AltimLog.Write("autostart", "Reading the Run key registration failed", ex);
        }

        IProcessMonitor? processes = null;
        try
        {
            processes = new WindowsProcessMonitor();
        }
        catch (Exception ex)
        {
            AltimLog.Write("processes", "Creating the process monitor failed; providers will build their own", ex);
        }

        var stack = new PlatformStack(notifications, autoStart)
        {
            Platform = platform,
            Processes = processes,
            NotificationsWork = notificationsWork,
        };

        if (toasts is not null)
        {
            stack._owned.Add(toasts);
        }

        if (platform is not null)
        {
            stack._owned.Add(platform);
        }

        return stack;
    }
#endif
}
