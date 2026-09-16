using Altim.Core.Models;

namespace Altim.Platform.Linux.DBus;

/// <summary>
/// The eight arguments <c>org.freedesktop.Notifications.Notify</c> takes, worked out from an
/// Altim <see cref="Notification"/> before any D-Bus type is involved.
/// </summary>
/// <param name="AppName">
/// The application name the daemon shows and groups by.
/// </param>
/// <param name="ReplacesId">
/// The id of a notification to replace, or zero to post a new one. This is how a rising
/// usage figure stays one banner instead of a stack of them.
/// </param>
/// <param name="AppIcon">
/// An icon theme name or an absolute file path. Empty means the daemon picks, usually from
/// <c>desktop-entry</c>.
/// </param>
/// <param name="Summary">The title line.</param>
/// <param name="Body">The detail line. Empty is allowed and common.</param>
/// <param name="ExpireTimeout">
/// Milliseconds before the banner is withdrawn; <c>-1</c> asks the daemon for its default
/// and <c>0</c> means never, which Altim does not use.
/// </param>
/// <param name="DesktopEntry">
/// The desktop file id, without its <c>.desktop</c> suffix, sent as the
/// <c>desktop-entry</c> hint so the daemon can find Altim's icon and name.
/// </param>
public sealed record FreedesktopNotificationRequest(
    string AppName,
    uint ReplacesId,
    string AppIcon,
    string Summary,
    string Body,
    int ExpireTimeout,
    string DesktopEntry)
{
    /// <summary>Ask the daemon to use its own timeout.</summary>
    public const int DefaultExpireTimeout = -1;

    /// <summary>The D-Bus signature of <c>Notify</c>. Wrong here means a rejected message.</summary>
    public const string NotifySignature = "susssasa{sv}i";

    /// <summary>
    /// Shapes a request from an Altim notification.
    /// </summary>
    /// <param name="notification">The message.</param>
    /// <param name="appName">The application name to present as.</param>
    /// <param name="desktopEntry">The desktop file id, without the <c>.desktop</c> suffix.</param>
    /// <param name="appIcon">An icon name or absolute path, or null for none.</param>
    /// <param name="replacesId">
    /// The id the daemon returned for the previous notification carrying the same
    /// <see cref="Notification.Tag"/>, or zero when there is none.
    /// </param>
    /// <returns>The arguments to send.</returns>
    /// <remarks>
    /// No provider name, file path or session detail is carried: what goes over the bus is
    /// the title and body that <c>Altim.Core</c> composed and nothing else.
    /// </remarks>
    public static FreedesktopNotificationRequest From(
        Notification notification,
        string appName,
        string desktopEntry,
        string? appIcon = null,
        uint replacesId = 0)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentNullException.ThrowIfNull(desktopEntry);

        return new FreedesktopNotificationRequest(
            appName,
            replacesId,
            appIcon ?? string.Empty,
            notification.Title,
            notification.Body,
            DefaultExpireTimeout,
            desktopEntry);
    }
}

/// <summary>
/// Remembers the id the daemon gave each tagged notification, so the next one with that tag
/// replaces it rather than stacking.
/// </summary>
/// <remarks>
/// <para>
/// The freedesktop protocol has no notion of a tag: replacement is by numeric id, and the id
/// is only known after the first <c>Notify</c> returns. This is the small amount of state
/// that turns <see cref="Notification.Tag"/> into that id.
/// </para>
/// <para>
/// An untagged notification is never remembered and never replaces anything, which is what a
/// null tag asks for. Nothing here is persisted: a restart correctly starts a new banner.
/// </para>
/// </remarks>
public sealed class NotificationReplacementMap
{
    private readonly Dictionary<string, uint> _idsByTag = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// The id to pass as <c>replaces_id</c> for a tag.
    /// </summary>
    /// <param name="tag">The notification's tag, or null.</param>
    /// <returns>The previous id, or zero for an untagged or first-time notification.</returns>
    public uint Resolve(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return 0;
        }

        lock (_gate)
        {
            return _idsByTag.TryGetValue(tag, out uint id) ? id : 0u;
        }
    }

    /// <summary>
    /// Records the id the daemon returned.
    /// </summary>
    /// <param name="tag">The notification's tag, or null to record nothing.</param>
    /// <param name="id">The id from <c>Notify</c>. Zero is not recorded.</param>
    public void Remember(string? tag, uint id)
    {
        if (string.IsNullOrEmpty(tag) || id == 0)
        {
            return;
        }

        lock (_gate)
        {
            _idsByTag[tag] = id;
        }
    }

    /// <summary>Forgets every recorded id, for a reconnect to a different daemon.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _idsByTag.Clear();
        }
    }
}
