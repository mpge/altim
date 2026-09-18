using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Altim.Platform.Linux;

/// <summary>
/// The Linux implementation of <see cref="IMotionPreferenceService"/>: the desktop's
/// <c>enable-animations</c> setting, read through the XDG desktop portal and watched for
/// changes on the session bus.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> There is no Linux desktop in the development environment. The match
/// rule and the value shapes are tested; nothing that touches a real bus is.
/// </para>
/// <para>
/// <b>Start-up is asynchronous and nothing waits for it.</b> The same rule
/// <see cref="LinuxPlatformService"/> follows, for the same reason: connecting to the
/// session bus and asking the portal for a value are round trips to a service that may still
/// be starting, and the tray icon's budget is under 800ms. The constructor starts the work
/// and returns with the preference at <see cref="MotionPreference.Unknown"/>;
/// <see cref="Ready"/> is there for a test or a diagnostic that wants to await it. The
/// practical consequence is that Altim starts without animating and turns motion on a moment
/// later if the desktop says motion is wanted, which is the right way round for this
/// particular unknown.
/// </para>
/// <para>
/// <b>Its own session-bus connection.</b> <see cref="LinuxNotificationService"/> already
/// opens one of its own rather than sharing the platform service's, and the same reasoning
/// applies here: this service is created, owned and disposed independently of whether a
/// panel host exists at all.
/// </para>
/// <para>
/// <b>A session that does not publish the setting stays unknown.</b> A portal with no
/// <c>org.gnome.desktop.interface</c> namespace answers the read with a D-Bus error, which
/// is not a failure — it is a desktop that has not told Altim anything about motion. See
/// <see cref="PortalAnimations"/> for why that is not rendered as "animate".
/// </para>
/// </remarks>
public sealed class LinuxMotionPreferenceService : IMotionPreferenceService, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();

    private DBusConnection? _sessionBus;
    private IDisposable? _subscription;
    private volatile MotionPreference _current;
    private bool _disposed;

    /// <summary>Starts the portal read and the subscription, and returns at once.</summary>
    public LinuxMotionPreferenceService()
    {
        _current = MotionPreference.Unknown;
        Ready = ConnectAsync(_shutdown.Token);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public MotionPreference Current => _current;

    /// <summary>
    /// Completes when the portal has been asked, successfully or not. Never faults: every
    /// failure is an unknown preference rather than an error.
    /// </summary>
    /// <remarks>
    /// Nothing in the application awaits this. It exists so a test can, and so a diagnostic
    /// can say whether the portal answered.
    /// </remarks>
    public Task Ready { get; }

    /// <summary>True when the portal answered with a value Altim could read.</summary>
    public bool PortalAnswered { get; private set; }

    /// <summary>Cancels the bus work, drops the subscription and closes the connection.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _shutdown.Cancel();

        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
            _sessionBus?.Dispose();
            _sessionBus = null;
        }

        _shutdown.Dispose();
    }

    /// <summary>
    /// Reads <c>SettingChanged(s namespace, s key, v value)</c> down to the animation flag.
    /// </summary>
    /// <returns>
    /// The flag, or <see langword="null"/> when the signal was about some other key in the
    /// namespace.
    /// </returns>
    private static bool? ReadSettingChanged(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        string settingNamespace = reader.ReadString();
        string key = reader.ReadString();

        if (!string.Equals(settingNamespace, DBusServices.GnomeInterfaceNamespace, StringComparison.Ordinal) ||
            !string.Equals(key, DBusServices.EnableAnimationsKey, StringComparison.Ordinal))
        {
            return null;
        }

        return PortalAnimations.TryRead(reader.ReadVariantValue(), out bool enabled) ? enabled : null;
    }

    private static bool? ReadEnableAnimationsReply(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        return PortalAnimations.TryRead(reader.ReadVariantValue(), out bool enabled) ? enabled : null;
    }

    private static async Task<bool?> ReadEnableAnimationsAsync(DBusConnection connection)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                DBusServices.PortalService,
                DBusServices.PortalPath,
                DBusServices.SettingsInterface,
                "Read",
                "ss");

            writer.WriteString(DBusServices.GnomeInterfaceNamespace);
            writer.WriteString(DBusServices.EnableAnimationsKey);
            message = writer.CreateMessage();
        }

        try
        {
            return await connection.CallMethodAsync(message, ReadEnableAnimationsReply).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            // The portal is there but does not carry this namespace: a backend that
            // implements only the freedesktop keys, or a desktop with no such setting at
            // all. Unknown, not "animate".
            return null;
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        // Yield first, so the constructor returns before any socket work starts.
        await Task.Yield();

        if (!OperatingSystem.IsLinux())
        {
            // Compiled into every build of the solution, so the guard belongs here as well
            // as in the composition root.
            return;
        }

        try
        {
            string? address = DBusAddress.Session;
            if (string.IsNullOrEmpty(address) || ct.IsCancellationRequested)
            {
                return;
            }

            var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);

            if (!TryKeep(connection))
            {
                connection.Dispose();
                return;
            }

            IDisposable subscription = await connection.AddMatchAsync<bool?>(
                DBusServices.AnimationsChangedRule(),
                ReadSettingChanged,
                OnSettingChanged,
                emitOnCapturedContext: false).ConfigureAwait(false);

            if (!TryKeepSubscription(subscription))
            {
                subscription.Dispose();
                return;
            }

            // Subscribe first, then read: the other order can miss a change that lands
            // between the two.
            bool? initial = await ReadEnableAnimationsAsync(connection).ConfigureAwait(false);
            PortalAnswered = initial is not null;

            Apply(PortalAnimations.ToPreference(initial));
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException
                                      or ObjectDisposedException or PlatformNotSupportedException
                                      or NotSupportedException or OperationCanceledException)
        {
            // No session bus, or no portal on it. The preference stays unknown, which is
            // what it is.
        }
    }

    private void OnSettingChanged(Notification<bool?> notification)
    {
        if (_disposed || notification.Type != NotificationType.Value || !notification.HasValue ||
            notification.Value is not { } enabled)
        {
            return;
        }

        Apply(PortalAnimations.ToPreference(enabled));
    }

    private void Apply(MotionPreference preference)
    {
        if (_disposed || preference == _current)
        {
            return;
        }

        _current = preference;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool TryKeep(DBusConnection connection)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _sessionBus = connection;
            return true;
        }
    }

    private bool TryKeepSubscription(IDisposable subscription)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _subscription = subscription;
            return true;
        }
    }
}
