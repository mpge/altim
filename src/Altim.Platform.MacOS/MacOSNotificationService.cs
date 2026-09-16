using System.Globalization;
using System.Runtime.InteropServices;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.MacOS.Interop;

namespace Altim.Platform.MacOS;

/// <summary>
/// Shows notifications through <c>UNUserNotificationCenter</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> None of this has run on macOS.
/// </para>
/// <para>
/// <b>The bundle requirement is a crash, not an error.</b>
/// <c>UNUserNotificationCenter.currentNotificationCenter</c> requires the process to have a
/// bundle identity. An unbundled process — which is what <c>dotnet run</c> produces, and
/// what a bare executable copied out of a <c>.app</c> produces — does not get a nil or an
/// <c>NSError</c> back: the framework raises an Objective-C exception, and a managed
/// <c>try</c>/<c>catch</c> cannot catch one. The process dies. So the bundle is checked
/// <em>before</em> the class is touched, in two independent ways — the executable's path
/// shape and <c>NSBundle.mainBundle.bundleIdentifier</c> — and the service reports itself
/// unavailable if either says no. <see cref="ShowAsync"/> then accepts every message and
/// drops it, which is exactly what <see cref="INotificationService"/> asks a platform
/// without notifications to do.
/// </para>
/// <para>
/// <b>Authorisation.</b> Notifications posted without permission are discarded silently, so
/// permission is requested once on construction. That call is the only place in the whole
/// macOS layer that needs an Objective-C block; see <see cref="ObjCBlock"/> for why it
/// cannot be nil. If the block cannot be built the request is skipped and the service still
/// runs — and, honestly, still shows nothing.
/// </para>
/// <para>
/// <b>Replacement rather than stacking.</b> A request identifier replaces an earlier
/// notification with the same identifier, so <see cref="Notification.Tag"/> is used as the
/// identifier directly. A message with no tag gets a fresh identifier and stacks, which is
/// what a null tag asks for.
/// </para>
/// </remarks>
public sealed unsafe class MacOSNotificationService : INotificationService
{
    /// <summary><c>UNAuthorizationOptionSound</c> and <c>UNAuthorizationOptionAlert</c>.</summary>
    private const nuint AuthorizationOptions = (1 << 1) | (1 << 2);

    /// <summary>Prefixes an untagged notification's identifier so Altim's are recognisable.</summary>
    private const string IdentifierPrefix = "altim.";

    private readonly IntPtr _center;
    private readonly string? _unavailableReason;

    /// <summary>
    /// Connects to the notification centre, or records why it could not.
    /// </summary>
    /// <remarks>
    /// Never throws. A machine with notifications switched off, an unbundled build and a
    /// host that is not macOS all end up in the same place: a service that accepts messages
    /// and drops them.
    /// </remarks>
    public MacOSNotificationService()
    {
        if (!OperatingSystem.IsMacOS())
        {
            _unavailableReason = "Notifications are only available on macOS.";
            return;
        }

        if (!IsBundled())
        {
            _unavailableReason =
                "Altim is not running from an application bundle, so macOS will not deliver its notifications.";
            return;
        }

        if (!NativeLibrary.TryLoad(
                "/System/Library/Frameworks/UserNotifications.framework/UserNotifications", out _))
        {
            _unavailableReason = "The UserNotifications framework could not be loaded.";
            return;
        }

        IntPtr centerClass = ObjC.Class("UNUserNotificationCenter");
        if (centerClass == IntPtr.Zero)
        {
            _unavailableReason = "UNUserNotificationCenter is not available on this version of macOS.";
            return;
        }

        IntPtr center = ObjC.Send(centerClass, ObjC.Selector("currentNotificationCenter"));
        if (center == IntPtr.Zero)
        {
            _unavailableReason = "macOS returned no notification centre for this process.";
            return;
        }

        _center = ObjC.Retain(center);
        RequestAuthorization();
    }

    /// <summary>
    /// Raised when a notification was not delivered. <see cref="ShowAsync"/> may not throw,
    /// so this is how a caller learns a threshold warning never reached the user.
    /// </summary>
    public event EventHandler<MacOSNotificationFailure>? DeliveryFailed;

    /// <summary>True when the notification centre was reachable.</summary>
    public bool IsAvailable => _center != IntPtr.Zero;

    /// <summary>Why notifications are unavailable, or null when they are not.</summary>
    public string? UnavailableReason => _unavailableReason;

    /// <summary>The outcome of the most recent attempt.</summary>
    public MacOSNotificationDelivery LastDelivery { get; private set; } = MacOSNotificationDelivery.None;

    /// <inheritdoc />
    public ValueTask ShowAsync(Notification n, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(n);

        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        if (_center == IntPtr.Zero)
        {
            Report(
                MacOSNotificationDelivery.Unavailable,
                _unavailableReason ?? "The notification centre is unavailable.");
            return ValueTask.CompletedTask;
        }

        IntPtr contentClass = ObjC.Class("UNMutableNotificationContent");
        IntPtr requestClass = ObjC.Class("UNNotificationRequest");
        if (contentClass == IntPtr.Zero || requestClass == IntPtr.Zero)
        {
            Report(MacOSNotificationDelivery.Unavailable, "The UserNotifications classes are not registered.");
            return ValueTask.CompletedTask;
        }

        IntPtr content = ObjC.Send(ObjC.Send(contentClass, ObjC.Selector("alloc")), ObjC.Selector("init"));
        if (content == IntPtr.Zero)
        {
            Report(MacOSNotificationDelivery.Failed, "macOS would not allocate the notification content.");
            return ValueTask.CompletedTask;
        }

        IntPtr title = ObjC.CreateString(n.Title);
        IntPtr body = ObjC.CreateString(n.Body);
        IntPtr identifier = ObjC.CreateString(BuildIdentifier(n));

        try
        {
            ObjC.Send(content, ObjC.Selector("setTitle:"), title);
            if (!string.IsNullOrEmpty(n.Body))
            {
                ObjC.Send(content, ObjC.Selector("setBody:"), body);
            }

            IntPtr request = ObjC.Send(
                requestClass,
                ObjC.Selector("requestWithIdentifier:content:trigger:"),
                identifier,
                content,
                IntPtr.Zero);

            if (request == IntPtr.Zero)
            {
                Report(MacOSNotificationDelivery.Failed, "macOS would not build the notification request.");
                return ValueTask.CompletedTask;
            }

            ObjC.Send(
                _center,
                ObjC.Selector("addNotificationRequest:withCompletionHandler:"),
                request,
                IntPtr.Zero);

            LastDelivery = MacOSNotificationDelivery.Submitted;
        }
        finally
        {
            ObjC.Release(identifier);
            ObjC.Release(body);
            ObjC.Release(title);
            ObjC.Release(content);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// True when this process has a real application bundle identity.
    /// </summary>
    /// <remarks>
    /// Both checks have to agree. The path test alone would accept an executable dropped by
    /// hand into a folder named <c>Foo.app/Contents/MacOS</c>, which has no <c>Info.plist</c>
    /// and therefore no bundle identifier; the identifier test alone would run before the
    /// path test and is the one that must never be reached unnecessarily.
    /// </remarks>
    private static bool IsBundled()
    {
        if (!MacOSAppBundle.LooksBundled(Environment.ProcessPath))
        {
            return false;
        }

        IntPtr bundleClass = ObjC.Class("NSBundle");
        if (bundleClass == IntPtr.Zero)
        {
            return false;
        }

        IntPtr bundle = ObjC.Send(bundleClass, ObjC.Selector("mainBundle"));
        if (bundle == IntPtr.Zero)
        {
            return false;
        }

        IntPtr identifier = ObjC.Send(bundle, ObjC.Selector("bundleIdentifier"));
        return !string.IsNullOrEmpty(ObjC.ReadString(identifier));
    }

    [UnmanagedCallersOnly]
    private static void AuthorizationCompleted(IntPtr block, byte granted, IntPtr error)
    {
        // Nothing to do. The result is not actionable: a refusal is the user's decision, and
        // the next ShowAsync reports the message as submitted either way because the
        // platform accepts it and then drops it. The handler exists because the API will not
        // take a nil block.
    }

    private static string BuildIdentifier(Notification n) =>
        string.IsNullOrEmpty(n.Tag)
            ? IdentifierPrefix + Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture)
            : n.Tag;

    private void RequestAuthorization()
    {
        IntPtr handler = ObjCBlock.CreateGlobal(
            (IntPtr)(delegate* unmanaged<IntPtr, byte, IntPtr, void>)&AuthorizationCompleted);

        if (handler == IntPtr.Zero)
        {
            // The block ABI could not be resolved. Asking with a nil handler is not an
            // option — the parameter is declared non-null and passing nil has been seen to
            // fault — so the request is skipped.
            return;
        }

        ObjC.SendRequestAuthorization(
            _center,
            ObjC.Selector("requestAuthorizationWithOptions:completionHandler:"),
            AuthorizationOptions,
            handler);
    }

    private void Report(MacOSNotificationDelivery delivery, string reason)
    {
        LastDelivery = delivery;
        DeliveryFailed?.Invoke(this, new MacOSNotificationFailure(delivery, reason));
    }
}

/// <summary>What happened to the most recent notification.</summary>
public enum MacOSNotificationDelivery
{
    /// <summary>Nothing has been attempted yet.</summary>
    None = 0,

    /// <summary>
    /// The platform accepted the request. macOS reports delivery asynchronously and only to
    /// a delegate, so this is a statement about the hand-off and not about the banner.
    /// </summary>
    Submitted = 1,

    /// <summary>
    /// There is no notification centre for this process: not macOS, not bundled, or the
    /// framework is missing.
    /// </summary>
    Unavailable = 2,

    /// <summary>The centre exists but would not build or accept the request.</summary>
    Failed = 3,
}

/// <summary>Describes a notification that was not delivered.</summary>
/// <param name="Delivery">Which failure occurred.</param>
/// <param name="Reason">A sentence suitable for a log or a settings page.</param>
public sealed record MacOSNotificationFailure(MacOSNotificationDelivery Delivery, string Reason);
