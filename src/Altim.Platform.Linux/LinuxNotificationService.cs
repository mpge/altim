using Altim.Core.Abstractions;
using Altim.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

// Tmds.DBus.Protocol has a Notification type of its own — the signal-observer callback
// envelope, nothing to do with a desktop notification. Aliasing Altim's keeps the one that
// matters unqualified and the collision impossible to write by accident.
using Notification = Altim.Core.Models.Notification;

namespace Altim.Platform.Linux;

/// <summary>
/// Shows notifications by calling <c>org.freedesktop.Notifications.Notify</c> on the session
/// bus.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> There is no Linux desktop in the development environment, so no
/// message built here has ever reached a notification daemon. The argument shaping is in
/// <see cref="FreedesktopNotificationRequest"/> and is tested; the wire call is not.
/// </para>
/// <para>
/// <b>Why D-Bus rather than libnotify.</b> libnotify is a C library that would have to be
/// present at the right soname on every distribution Altim runs on, and it pulls GLib's main
/// loop in with it. The protocol underneath it is a single well-known method call, so Altim
/// makes the call itself and has no native dependency at all beyond the session bus that a
/// desktop session already has.
/// </para>
/// <para>
/// <b>Everything degrades.</b> No session bus, no notification daemon, a daemon that rejects
/// the call — each is recorded on <see cref="LastDelivery"/> and raised on
/// <see cref="DeliveryFailed"/>. <see cref="ShowAsync"/> never throws, because a machine with
/// no notifications must still run Altim.
/// </para>
/// <para>
/// This owns its own connection rather than sharing one with
/// <see cref="LinuxPlatformService"/>. Two session-bus connections cost two Unix sockets and
/// keep the notification path from being taken down by a portal failure, or the other way
/// round.
/// </para>
/// </remarks>
public sealed class LinuxNotificationService : INotificationService, IDisposable
{
    private readonly NotificationReplacementMap _replacements = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly string _appName;
    private readonly string _desktopEntry;
    private readonly string _appIcon;

    private DBusConnection? _connection;
    private string? _unavailableReason;
    private bool _disposed;

    /// <summary>
    /// Creates the service. Nothing connects until the first notification is shown, so a
    /// session with no bus costs nothing at start-up.
    /// </summary>
    /// <param name="appName">The name the daemon shows and groups by.</param>
    /// <param name="desktopEntry">
    /// The desktop file id without its <c>.desktop</c> suffix, sent as the
    /// <c>desktop-entry</c> hint so the daemon can find Altim's icon.
    /// </param>
    /// <param name="appIcon">
    /// An icon theme name or absolute path, or null to let the daemon resolve one from
    /// <paramref name="desktopEntry"/>.
    /// </param>
    public LinuxNotificationService(string appName = "Altim", string desktopEntry = "altim", string? appIcon = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentNullException.ThrowIfNull(desktopEntry);

        _appName = appName;
        _desktopEntry = desktopEntry;
        _appIcon = appIcon ?? string.Empty;
    }

    /// <summary>
    /// Raised when a notification was not delivered. <see cref="ShowAsync"/> may not throw,
    /// so this is how a caller learns a threshold warning never reached the user.
    /// </summary>
    public event EventHandler<LinuxNotificationFailure>? DeliveryFailed;

    /// <summary>The outcome of the most recent attempt.</summary>
    public LinuxNotificationDelivery LastDelivery { get; private set; } = LinuxNotificationDelivery.None;

    /// <summary>
    /// Why notifications are unavailable, or null when nothing has gone wrong yet. Populated
    /// on the first failed attempt, because nothing is probed before then.
    /// </summary>
    public string? UnavailableReason => _unavailableReason;

    /// <inheritdoc />
    public async ValueTask ShowAsync(Notification n, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(n);
        ct.ThrowIfCancellationRequested();

        if (_disposed)
        {
            Report(LinuxNotificationDelivery.Unavailable, "The notification service has been disposed.");
            return;
        }

        DBusConnection? connection = await EnsureConnectionAsync(ct).ConfigureAwait(false);
        if (connection is null)
        {
            Report(
                LinuxNotificationDelivery.Unavailable,
                _unavailableReason ?? "The session bus is unavailable.");
            return;
        }

        FreedesktopNotificationRequest request = FreedesktopNotificationRequest.From(
            n, _appName, _desktopEntry, _appIcon.Length == 0 ? null : _appIcon, _replacements.Resolve(n.Tag));

        try
        {
            uint id = await NotifyAsync(connection, request).ConfigureAwait(false);
            _replacements.Remember(n.Tag, id);
            LastDelivery = LinuxNotificationDelivery.Shown;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or ObjectDisposedException or IOException
                                      or InvalidOperationException)
        {
            Report(LinuxNotificationDelivery.Rejected, ex.Message);
        }
    }

    /// <summary>Closes the session bus connection.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection?.Dispose();
        _connection = null;
        _connectGate.Dispose();
    }

    private static async Task<uint> NotifyAsync(DBusConnection connection, FreedesktopNotificationRequest request)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                DBusServices.NotificationsService,
                DBusServices.NotificationsPath,
                DBusServices.NotificationsInterface,
                "Notify",
                FreedesktopNotificationRequest.NotifySignature);

            writer.WriteString(request.AppName);
            writer.WriteUInt32(request.ReplacesId);
            writer.WriteString(request.AppIcon);
            writer.WriteString(request.Summary);
            writer.WriteString(request.Body);

            // No actions. Altim's notifications are informational: a button would need the
            // daemon to call back into a process that may have been restarted since.
            writer.WriteArray(Array.Empty<string>());

            writer.WriteDictionary(new Dictionary<string, VariantValue>(StringComparer.Ordinal)
            {
                // Lets the daemon find the icon and the application name from the same
                // desktop entry the autostart registration writes.
                ["desktop-entry"] = VariantValue.String(request.DesktopEntry),

                // 1 is "normal". Altim never raises a critical notification: critical
                // banners on most desktops do not expire, and a usage warning is not worth
                // a permanent one.
                ["urgency"] = VariantValue.Byte(1),
            });

            writer.WriteInt32(request.ExpireTimeout);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, ReadNotificationId).ConfigureAwait(false);
    }

    private static uint ReadNotificationId(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        return reader.ReadUInt32();
    }

    private async ValueTask<DBusConnection?> EnsureConnectionAsync(CancellationToken ct)
    {
        DBusConnection? existing = _connection;
        if (existing is not null)
        {
            return existing;
        }

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            if (!OperatingSystem.IsLinux())
            {
                _unavailableReason = "The freedesktop notification daemon is only used on Linux.";
                return null;
            }

            string? address = DBusAddress.Session;
            if (string.IsNullOrEmpty(address))
            {
                _unavailableReason =
                    "No session bus address; Altim is running outside a desktop session, so it cannot notify.";
                return null;
            }

            var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            _connection = connection;
            return connection;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException
                                      or PlatformNotSupportedException or NotSupportedException)
        {
            _unavailableReason = ex.Message;
            return null;
        }
        finally
        {
            _ = _connectGate.Release();
        }
    }

    private void Report(LinuxNotificationDelivery delivery, string reason)
    {
        LastDelivery = delivery;
        _unavailableReason ??= reason;
        DeliveryFailed?.Invoke(this, new LinuxNotificationFailure(delivery, reason));
    }
}

/// <summary>What happened to the most recent notification.</summary>
public enum LinuxNotificationDelivery
{
    /// <summary>Nothing has been attempted yet.</summary>
    None = 0,

    /// <summary>The daemon accepted the message and returned an id.</summary>
    Shown = 1,

    /// <summary>There is no session bus, or no connection could be made to it.</summary>
    Unavailable = 2,

    /// <summary>The bus is there but the call failed or no daemon answered it.</summary>
    Rejected = 3,
}

/// <summary>Describes a notification that was not delivered.</summary>
/// <param name="Delivery">Which failure occurred.</param>
/// <param name="Reason">A sentence suitable for a log or a settings page.</param>
public sealed record LinuxNotificationFailure(LinuxNotificationDelivery Delivery, string Reason);
