using Altim.App.Diagnostics;
using Altim.App.Services;
using Altim.Core.Abstractions;
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
/// The platform is chosen inside <c>#if WINDOWS</c> rather than only by
/// <c>OperatingSystem.IsWindows()</c>, because the Windows types exist exclusively in
/// <c>Altim.Platform.Windows</c>' Windows-flavoured target framework. macOS and Linux have
/// no implementations yet — both projects are empty — so both currently take the fallback,
/// which is a running application with no tray presence.
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
