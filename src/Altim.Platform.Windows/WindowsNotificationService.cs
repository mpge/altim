#if WINDOWS

using System.Security.Principal;
using Altim.Core.Abstractions;
using Altim.Core.Diagnostics;
using Altim.Core.Models;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Altim.Platform.Windows;

/// <summary>
/// Shows toasts through the Windows App SDK's <c>AppNotificationManager</c>, running
/// unpackaged.
/// </summary>
/// <remarks>
/// <para>
/// Unpackaged means no AUMID and no Start menu shortcut is registered by Altim.
/// <c>AppNotificationManager.Default.Register()</c> is the whole registration: the
/// Windows App SDK derives an identity from the executable and registers the COM
/// activator itself. The overload that takes a display name and icon is deliberately
/// not used, because it writes a shortcut into the user's Start menu.
/// </para>
/// <para>
/// <b>Toasts do not appear when the process is elevated.</b> The notification platform
/// refuses the request from a high integrity process and reports success anyway, so a
/// service that trusted the return value would claim to have warned the user when it
/// had not. Elevation is therefore detected up front and reported through
/// <see cref="LastDelivery"/> and <see cref="DeliveryFailed"/>, which is also why
/// <see cref="ShowAsync"/> keeps to its contract of never throwing.
/// </para>
/// </remarks>
public sealed class WindowsNotificationService : INotificationService, IDisposable
{
    /// <summary>Groups Altim's toasts so a tag replaces within Altim only.</summary>
    private const string NotificationGroup = "altim";

    private readonly bool _elevated;
    private readonly bool _registered;
    private readonly string? _registrationError;
    private bool _disposed;

    /// <summary>
    /// Registers with the notification platform. Registration failure is recorded
    /// rather than thrown: a machine with notifications switched off must still run.
    /// </summary>
    public WindowsNotificationService()
    {
        _elevated = IsElevated();

        if (_elevated)
        {
            _registrationError = "The process is elevated; Windows silently discards app notifications from an elevated process.";
            LastDelivery = WindowsNotificationDelivery.SuppressedByElevation;
            return;
        }

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            _registrationError =
                "The notification platform is unavailable, so usage alerts are off ("
                    + ExceptionSummary.Describe(ex)
                    + ").";
            LastDelivery = WindowsNotificationDelivery.NotRegistered;
        }
    }

    /// <summary>
    /// Raised when a notification was not delivered. The contract forbids throwing
    /// from <see cref="ShowAsync"/>, so this is how a caller learns that a threshold
    /// warning never reached the user.
    /// </summary>
    public event EventHandler<WindowsNotificationFailure>? DeliveryFailed;

    /// <summary>The outcome of the most recent attempt.</summary>
    public WindowsNotificationDelivery LastDelivery { get; private set; } = WindowsNotificationDelivery.None;

    /// <summary>True when the notification platform accepted the registration.</summary>
    public bool IsRegistered => _registered;

    /// <summary>
    /// True when this process is elevated, in which case no notification this service
    /// shows will ever be seen.
    /// </summary>
    public bool IsSuppressedByElevation => _elevated;

    /// <summary>Why registration failed, or null when it succeeded.</summary>
    public string? RegistrationError => _registrationError;

    /// <inheritdoc />
    public ValueTask ShowAsync(Notification n, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(n);

        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        if (_disposed)
        {
            Report(WindowsNotificationDelivery.NotRegistered, "The notification service has been disposed.");
            return ValueTask.CompletedTask;
        }

        if (_elevated)
        {
            Report(
                WindowsNotificationDelivery.SuppressedByElevation,
                "Windows discards app notifications raised by an elevated process. Run Altim without elevation to see alerts.");
            return ValueTask.CompletedTask;
        }

        if (!_registered)
        {
            Report(WindowsNotificationDelivery.NotRegistered, _registrationError ?? "The notification platform is unavailable.");
            return ValueTask.CompletedTask;
        }

        try
        {
            var builder = new AppNotificationBuilder().AddText(n.Title);
            if (!string.IsNullOrEmpty(n.Body))
            {
                _ = builder.AddText(n.Body);
            }

            AppNotification notification = builder.BuildNotification();

            // A tag replaces the earlier toast with the same tag instead of stacking a
            // second one, which is what keeps a rising usage figure to one alert.
            if (!string.IsNullOrEmpty(n.Tag))
            {
                notification.Tag = n.Tag;
                notification.Group = NotificationGroup;
            }

            AppNotificationManager.Default.Show(notification);

            if (notification.Id == 0)
            {
                Report(WindowsNotificationDelivery.Rejected, "The notification platform rejected the toast without an error.");
            }
            else
            {
                LastDelivery = WindowsNotificationDelivery.Shown;
            }
        }
        catch (Exception ex)
        {
            Report(
                WindowsNotificationDelivery.Failed,
                "The notification platform did not show the alert ("
                    + ExceptionSummary.Describe(ex)
                    + ").");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Unregisters from the notification platform.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception)
        {
            // Shutdown is not a place to raise: the registration dies with the process.
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Report(WindowsNotificationDelivery delivery, string reason)
    {
        LastDelivery = delivery;
        DeliveryFailed?.Invoke(this, new WindowsNotificationFailure(delivery, reason));
    }
}

/// <summary>What happened to the most recent notification.</summary>
public enum WindowsNotificationDelivery
{
    /// <summary>Nothing has been attempted yet.</summary>
    None = 0,

    /// <summary>The platform accepted the toast.</summary>
    Shown = 1,

    /// <summary>
    /// The process is elevated. Windows discards the toast and reports no error, so
    /// this is the only honest answer available.
    /// </summary>
    SuppressedByElevation = 2,

    /// <summary>Registration with the notification platform failed or was not attempted.</summary>
    NotRegistered = 3,

    /// <summary>The platform accepted the call but assigned the toast no identifier.</summary>
    Rejected = 4,

    /// <summary>The call threw.</summary>
    Failed = 5,
}

/// <summary>Describes a notification that was not delivered.</summary>
/// <param name="Delivery">Which failure occurred.</param>
/// <param name="Reason">A sentence suitable for a log or a settings page.</param>
public sealed record WindowsNotificationFailure(WindowsNotificationDelivery Delivery, string Reason);

#endif
