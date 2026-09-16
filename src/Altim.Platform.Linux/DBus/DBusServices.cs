using Tmds.DBus.Protocol;

namespace Altim.Platform.Linux.DBus;

/// <summary>
/// The bus names, object paths and interfaces Altim talks to on Linux, and the match rules
/// it subscribes with.
/// </summary>
/// <remarks>
/// <para>
/// Three services, on two buses. Notifications and the appearance portal are on the
/// <em>session</em> bus; logind is on the <em>system</em> bus. Subscribing to a signal on
/// the wrong bus is accepted without complaint and then never fires, which is the same class
/// of silent failure as observing the wrong notification centre on macOS, so the bus each
/// rule belongs to is named here rather than at the call site.
/// </para>
/// <para>
/// The match rules are built as data so the fields can be asserted in a test. Whether the
/// signals actually arrive can only be established on a Linux desktop.
/// </para>
/// </remarks>
public static class DBusServices
{
    /// <summary>The freedesktop notification daemon's well-known name. Session bus.</summary>
    public const string NotificationsService = "org.freedesktop.Notifications";

    /// <summary>The notification daemon's object path.</summary>
    public const string NotificationsPath = "/org/freedesktop/Notifications";

    /// <summary>The notification daemon's interface, which shares its name with the service.</summary>
    public const string NotificationsInterface = "org.freedesktop.Notifications";

    /// <summary>systemd-logind's well-known name. System bus.</summary>
    public const string LoginManagerService = "org.freedesktop.login1";

    /// <summary>The login manager object.</summary>
    public const string LoginManagerPath = "/org/freedesktop/login1";

    /// <summary>The login manager interface carrying <c>PrepareForSleep</c>.</summary>
    public const string LoginManagerInterface = "org.freedesktop.login1.Manager";

    /// <summary>
    /// Emitted twice per sleep: with <c>true</c> just before suspending and with
    /// <c>false</c> once the machine has come back.
    /// </summary>
    public const string PrepareForSleepSignal = "PrepareForSleep";

    /// <summary>The XDG desktop portal's well-known name. Session bus.</summary>
    public const string PortalService = "org.freedesktop.portal.Desktop";

    /// <summary>The portal object.</summary>
    public const string PortalPath = "/org/freedesktop/portal/desktop";

    /// <summary>The portal's settings interface.</summary>
    public const string SettingsInterface = "org.freedesktop.portal.Settings";

    /// <summary>Emitted when a portal setting changes, as <c>(s namespace, s key, v value)</c>.</summary>
    public const string SettingChangedSignal = "SettingChanged";

    /// <summary>The namespace carrying the desktop's light or dark preference.</summary>
    public const string AppearanceNamespace = "org.freedesktop.appearance";

    /// <summary>The key inside that namespace.</summary>
    public const string ColorSchemeKey = "color-scheme";

    /// <summary>
    /// The rule that delivers logind's wake signal. <b>System bus.</b>
    /// </summary>
    /// <returns>A match rule narrowed to the one signal, so no other traffic is woken for.</returns>
    public static MatchRule PrepareForSleepRule() => new()
    {
        Type = MessageType.Signal,
        Sender = LoginManagerService,
        Path = LoginManagerPath,
        Interface = LoginManagerInterface,
        Member = PrepareForSleepSignal,
    };

    /// <summary>
    /// The rule that delivers the portal's appearance changes. <b>Session bus.</b>
    /// </summary>
    /// <returns>
    /// A match rule narrowed to the appearance namespace by its first argument, so a
    /// desktop that changes unrelated portal settings does not wake Altim.
    /// </returns>
    public static MatchRule AppearanceChangedRule() => new()
    {
        Type = MessageType.Signal,
        Sender = PortalService,
        Path = PortalPath,
        Interface = SettingsInterface,
        Member = SettingChangedSignal,
        Arg0 = AppearanceNamespace,
    };
}
