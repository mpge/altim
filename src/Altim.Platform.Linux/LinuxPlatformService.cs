using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Altim.Platform.Linux;

/// <summary>
/// The Linux implementation of <see cref="IPlatformService"/>: the panel host, logind's wake
/// signal, and the appearance portal's light-or-dark preference.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> There is no Linux desktop in the development environment. The match
/// rules and the portal value handling are tested; nothing that touches a real bus is.
/// </para>
/// <para>
/// <b>There is no tray anchor on Linux, ever.</b> See
/// <see cref="GetTrayAnchorAsync"/>.
/// </para>
/// <para>
/// <b>One subscription carries both halves of a sleep.</b> logind's <c>PrepareForSleep</c>
/// is emitted with <c>true</c> before the machine suspends and <c>false</c> when it comes
/// back, so <see cref="SystemSuspending"/> and <see cref="SystemResumed"/> come from the
/// same match rule. Unlike Windows and macOS there is no second source and no display-state
/// signal to confuse it with.
/// </para>
/// <para>
/// <b>Two buses.</b> <c>PrepareForSleep</c> is a system-bus signal from logind;
/// <c>SettingChanged</c> is a session-bus signal from the portal. Subscribing on the wrong
/// bus is accepted and then never fires, so each subscription names its bus at the call site
/// and the rules themselves live in <see cref="DBusServices"/>.
/// </para>
/// <para>
/// <b>Start-up is asynchronous and nothing waits for it.</b> Connecting to two buses and
/// asking the portal for a value are round trips to services that may still be starting
/// themselves, and Altim's budget is under 800ms to a visible tray icon. The constructor
/// therefore kicks the work off and returns; <see cref="Ready"/> is there for a test or a
/// diagnostic that wants to await it. The practical consequence is the one the appearance
/// portal has anyway: Altim starts light and corrects itself a moment later if the desktop
/// is dark, which raises <see cref="ThemeChanged"/> once with no user involvement.
/// </para>
/// <para>
/// <b>Everything degrades.</b> No system bus means no wake signal — the 60s polling floor
/// still covers a resumed machine, just less promptly. No portal means no preference, which
/// is light. Neither is an error and neither is reported as one.
/// </para>
/// </remarks>
public sealed class LinuxPlatformService : IPlatformService, IDisposable
{
    /// <summary>
    /// Two wake signals inside this window are the same wake — the same rule the Windows and
    /// macOS services use.
    /// </summary>
    private static readonly TimeSpan ResumeCoalescingWindow = TimeSpan.FromSeconds(5);

    private readonly LinuxTrayHost _tray;
    private readonly bool _ownsTray;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _subscriptionGate = new();

    private DBusConnection? _systemBus;
    private DBusConnection? _sessionBus;
    private long _lastResumeTicks;
    private long _lastSuspendTicks;
    private volatile bool _dark;
    private bool _disposed;

    /// <summary>Creates the platform service and its panel host.</summary>
    /// <param name="tooltip">Initial panel tooltip.</param>
    /// <param name="assetDirectory">
    /// Directory holding the icon assets, or <see langword="null"/> to discover it.
    /// </param>
    public LinuxPlatformService(string tooltip = "Altim", string? assetDirectory = null)
        : this(new LinuxTrayHost(tooltip, assetDirectory), ownsTray: true)
    {
    }

    /// <summary>Creates the platform service over an existing panel host.</summary>
    /// <param name="tray">The tray host to expose.</param>
    /// <param name="ownsTray">
    /// True when disposing this service should also dispose <paramref name="tray"/>.
    /// <see cref="IPlatformService"/> says the service owns the tray, so this is only false
    /// when a test wants the host to outlive the service.
    /// </param>
    public LinuxPlatformService(LinuxTrayHost tray, bool ownsTray = true)
    {
        ArgumentNullException.ThrowIfNull(tray);

        _tray = tray;
        _ownsTray = ownsTray;
        Ready = ConnectAsync(_shutdown.Token);
    }

    /// <inheritdoc />
    public event EventHandler? SystemSuspending;

    /// <inheritdoc />
    public event EventHandler? SystemResumed;

    /// <inheritdoc />
    public event EventHandler? ThemeChanged;

    /// <inheritdoc />
    public ITrayHost Tray => _tray;

    /// <summary>
    /// Completes when the bus subscriptions have been attempted, successfully or not. Never
    /// faults: every failure is a degraded capability rather than an error.
    /// </summary>
    /// <remarks>
    /// Nothing in the application needs to await this. It exists so a test can, and so a
    /// diagnostic can say whether the signals are live.
    /// </remarks>
    public Task Ready { get; }

    /// <summary>True when the appearance portal reports a dark preference.</summary>
    public bool IsDarkTheme => _dark;

    /// <summary>True when logind's wake signal was subscribed to.</summary>
    public bool SleepSignalConnected { get; private set; }

    /// <summary>True when the appearance portal answered.</summary>
    public bool AppearancePortalConnected { get; private set; }

    /// <summary>
    /// Always <see langword="null"/> on Linux.
    /// </summary>
    /// <returns>
    /// <see langword="null"/>, in every session, on every desktop, forever.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is not a gap waiting to be filled in. The StatusNotifierItem specification
    /// describes a D-Bus object with a title, an icon, a status and a menu, and contains no
    /// geometry of any kind: the panel decides where to draw the item, may re-order it, may
    /// move it into an overflow, and never tells the item about any of that. There is no
    /// call to make, on any desktop, that would answer the question — which is why
    /// ARCHITECTURE.md's platform matrix records "no anchor exists in the protocol" rather
    /// than "not implemented".
    /// </para>
    /// <para>
    /// <see cref="Core.Abstractions.IPlatformService.GetTrayAnchorAsync"/> already documents
    /// null as the normal Linux answer. The caller drops through the positioning tiers: the
    /// cursor position is not used either, because a StatusNotifierItem click is delivered
    /// over D-Bus without one, so the panel lands in the working-area corner.
    /// </para>
    /// </remarks>
    public ValueTask<PixelRect?> GetTrayAnchorAsync() => ValueTask.FromResult<PixelRect?>(null);

    /// <summary>Cancels the bus work, drops every subscription and disposes the panel host.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _shutdown.Cancel();

        lock (_subscriptionGate)
        {
            foreach (IDisposable subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        _systemBus?.Dispose();
        _systemBus = null;
        _sessionBus?.Dispose();
        _sessionBus = null;

        _shutdown.Dispose();

        if (_ownsTray)
        {
            _tray.Dispose();
        }
    }

    /// <summary>
    /// Reads the boolean carried by <c>PrepareForSleep</c>.
    /// </summary>
    /// <remarks>
    /// True means "about to suspend" and false means "back". Altim only reacts to the second;
    /// the scheduler pauses on its own when nothing wakes it.
    /// </remarks>
    private static bool ReadPrepareForSleep(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        return reader.ReadBool();
    }

    /// <summary>
    /// Reads <c>SettingChanged(s namespace, s key, v value)</c> down to the colour scheme.
    /// </summary>
    /// <returns>
    /// The colour scheme, or <see langword="null"/> when the signal was about some other key.
    /// </returns>
    private static uint? ReadSettingChanged(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        string settingNamespace = reader.ReadString();
        string key = reader.ReadString();

        if (!string.Equals(settingNamespace, DBusServices.AppearanceNamespace, StringComparison.Ordinal) ||
            !string.Equals(key, DBusServices.ColorSchemeKey, StringComparison.Ordinal))
        {
            return null;
        }

        return PortalAppearance.TryReadColorScheme(reader.ReadVariantValue(), out uint scheme) ? scheme : null;
    }

    private static uint? ReadColorSchemeReply(Message message, object? state)
    {
        Reader reader = message.GetBodyReader();
        return PortalAppearance.TryReadColorScheme(reader.ReadVariantValue(), out uint scheme) ? scheme : null;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        // Yield first so the constructor returns before any socket work starts: the tray
        // icon has an 800ms budget and must not queue behind a bus that is still coming up.
        await Task.Yield();

        if (!OperatingSystem.IsLinux())
        {
            // Compiled into every build of the solution — see the project file — so the guard
            // belongs here rather than only in the composition root.
            return;
        }

        await SubscribeToSleepAsync(ct).ConfigureAwait(false);
        await SubscribeToAppearanceAsync(ct).ConfigureAwait(false);
    }

    private async Task SubscribeToSleepAsync(CancellationToken ct)
    {
        try
        {
            string? address = DBusAddress.System;
            if (string.IsNullOrEmpty(address) || ct.IsCancellationRequested)
            {
                return;
            }

            var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);

            if (!TryKeep(connection, ref _systemBus))
            {
                connection.Dispose();
                return;
            }

            IDisposable subscription = await connection.AddMatchAsync<bool>(
                DBusServices.PrepareForSleepRule(),
                ReadPrepareForSleep,
                OnPrepareForSleep,
                emitOnCapturedContext: false).ConfigureAwait(false);

            if (!TryAddSubscription(subscription))
            {
                subscription.Dispose();
                return;
            }

            SleepSignalConnected = true;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException
                                      or ObjectDisposedException or PlatformNotSupportedException
                                      or NotSupportedException or OperationCanceledException)
        {
            // No system bus, or no logind on it. The 60s polling floor still covers a machine
            // that woke up, just less promptly than a signal would.
        }
    }

    private async Task SubscribeToAppearanceAsync(CancellationToken ct)
    {
        try
        {
            string? address = DBusAddress.Session;
            if (string.IsNullOrEmpty(address) || ct.IsCancellationRequested)
            {
                return;
            }

            var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);

            if (!TryKeep(connection, ref _sessionBus))
            {
                connection.Dispose();
                return;
            }

            IDisposable subscription = await connection.AddMatchAsync<uint?>(
                DBusServices.AppearanceChangedRule(),
                ReadSettingChanged,
                OnSettingChanged,
                emitOnCapturedContext: false).ConfigureAwait(false);

            if (!TryAddSubscription(subscription))
            {
                subscription.Dispose();
                return;
            }

            // Subscribe first, then read: the other order can miss a change that lands
            // between the two.
            uint? initial = await ReadColorSchemeAsync(connection).ConfigureAwait(false);
            AppearancePortalConnected = true;

            if (initial is { } scheme)
            {
                await ApplyColorSchemeAsync(scheme).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or InvalidOperationException
                                      or ObjectDisposedException or PlatformNotSupportedException
                                      or NotSupportedException or OperationCanceledException)
        {
            // No portal, or a portal too old to carry the appearance namespace. No preference
            // means light, which is already the state.
        }
    }

    private static async Task<uint?> ReadColorSchemeAsync(DBusConnection connection)
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

            writer.WriteString(DBusServices.AppearanceNamespace);
            writer.WriteString(DBusServices.ColorSchemeKey);
            message = writer.CreateMessage();
        }

        try
        {
            return await connection.CallMethodAsync(message, ReadColorSchemeReply).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            // The portal is there but has no appearance namespace: an older xdg-desktop-portal,
            // or a desktop that does not implement the Settings interface. No preference.
            return null;
        }
    }

    private void OnPrepareForSleep(Notification<bool> notification)
    {
        if (_disposed || notification.Type != NotificationType.Value || !notification.HasValue)
        {
            return;
        }

        // The signal fires twice per sleep, and Linux is the one platform that gets both
        // halves from one subscription: true is "about to suspend", false is "back". The
        // scheduler pauses on the first and refreshes once on the second.
        if (notification.Value)
        {
            RaiseSuspend();
            return;
        }

        RaiseResume();
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

    private void OnSettingChanged(Notification<uint?> notification)
    {
        if (_disposed || notification.Type != NotificationType.Value || !notification.HasValue ||
            notification.Value is not { } scheme)
        {
            return;
        }

        _ = ApplyColorSchemeAsync(scheme);
    }

    private async Task ApplyColorSchemeAsync(uint colorScheme)
    {
        bool dark = PortalAppearance.IsDark(colorScheme);
        if (dark == _dark)
        {
            return;
        }

        _dark = dark;

        // The glyph follows the desktop preference because the panel's own background is not
        // knowable; see LinuxTrayAssets.
        try
        {
            await _tray.SetDesktopColorSchemeAsync(dark).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
                                      or TaskCanceledException)
        {
            // The dispatcher is gone, or the host is disposed. The theme event still fires.
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryKeep(DBusConnection connection, ref DBusConnection? slot)
    {
        lock (_subscriptionGate)
        {
            if (_disposed)
            {
                return false;
            }

            slot = connection;
            return true;
        }
    }

    private bool TryAddSubscription(IDisposable subscription)
    {
        lock (_subscriptionGate)
        {
            if (_disposed)
            {
                return false;
            }

            _subscriptions.Add(subscription);
            return true;
        }
    }
}
