using System.Runtime.InteropServices;
using Altim.Core.Abstractions;
using Altim.Platform.MacOS.Interop;

namespace Altim.Platform.MacOS;

/// <summary>
/// The four states <c>SMAppService</c> reports. The numeric values are the ones
/// <c>SMAppServiceStatus</c> defines.
/// </summary>
public enum MacOSLoginItemStatus
{
    /// <summary>The login item has never been registered.</summary>
    NotRegistered = 0,

    /// <summary>Registered and approved: Altim will start with the session.</summary>
    Enabled = 1,

    /// <summary>
    /// Registered, but the user has not approved it in System Settings ▸ General ▸ Login
    /// Items. Altim will <em>not</em> start until they do, so this is reported as off.
    /// </summary>
    RequiresApproval = 2,

    /// <summary>macOS cannot find the bundle to register.</summary>
    NotFound = 3,

    /// <summary>
    /// <c>SMAppService</c> itself is unavailable — macOS 12 or older, or an unbundled build.
    /// Not one of Apple's values; Altim's own.
    /// </summary>
    Unavailable = -1,
}

/// <summary>
/// Start with the user session, through <c>SMAppService.mainApp</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> None of this has run on macOS.
/// </para>
/// <para>
/// <b>State is read from the service, never cached.</b> The user can switch Altim off in
/// System Settings ▸ General ▸ Login Items at any moment, and <c>SMAppService</c> is the
/// only thing that knows. An implementation that remembered what it last wrote would tell
/// the user Altim starts at login while macOS has it switched off — the same bug the
/// Windows implementation avoids by reading <c>StartupApproved</c>. Every
/// <see cref="IsEnabledAsync"/> therefore asks the service again.
/// </para>
/// <para>
/// <b>Only <see cref="MacOSLoginItemStatus.Enabled"/> counts as on.</b>
/// <c>requiresApproval</c> means the registration exists but macOS is waiting for the user,
/// and Altim will not actually start; reporting that as "enabled" would be a lie in the one
/// place a user checks.
/// </para>
/// <para>
/// <b>Requirements.</b> <c>SMAppService</c> arrived in macOS 13 and needs a real application
/// bundle. Both are checked, and a machine that fails either gets a service that reports
/// false and accepts a write that does nothing —
/// <see cref="IAutoStartService"/> already tells callers to read the state back.
/// </para>
/// </remarks>
public sealed class MacOSAutoStartService : IAutoStartService
{
    private readonly IntPtr _service;
    private readonly string? _unavailableReason;

    /// <summary>
    /// Resolves <c>SMAppService.mainApp</c>, or records why it could not.
    /// </summary>
    public MacOSAutoStartService()
    {
        if (!OperatingSystem.IsMacOS())
        {
            _unavailableReason = "Start at login is only available on macOS.";
            return;
        }

        if (!MacOSAppBundle.LooksBundled(Environment.ProcessPath))
        {
            _unavailableReason =
                "Altim is not running from an application bundle, so macOS cannot register it as a login item.";
            return;
        }

        if (!NativeLibrary.TryLoad(
                "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement", out _))
        {
            _unavailableReason = "The ServiceManagement framework could not be loaded.";
            return;
        }

        IntPtr serviceClass = ObjC.Class("SMAppService");
        if (serviceClass == IntPtr.Zero)
        {
            _unavailableReason = "SMAppService requires macOS 13 or newer.";
            return;
        }

        // Swift's SMAppService.mainApp is +[SMAppService mainAppService] in Objective-C.
        IntPtr service = ObjC.Send(serviceClass, ObjC.Selector("mainAppService"));
        if (service == IntPtr.Zero)
        {
            _unavailableReason = "macOS returned no login item service for this bundle.";
            return;
        }

        _service = ObjC.Retain(service);
    }

    /// <summary>True when <c>SMAppService</c> could be reached at all.</summary>
    public bool IsSupported => _service != IntPtr.Zero;

    /// <summary>Why start at login is unavailable, or null when it is not.</summary>
    public string? UnavailableReason => _unavailableReason;

    /// <summary>
    /// The raw state macOS reports, which distinguishes "never registered" from "waiting for
    /// the user to approve it" — a distinction a settings page can usefully show.
    /// </summary>
    public MacOSLoginItemStatus Status => _service == IntPtr.Zero
        ? MacOSLoginItemStatus.Unavailable
        : (MacOSLoginItemStatus)ObjC.SendLong(_service, ObjC.Selector("status"));

    /// <inheritdoc />
    public ValueTask<bool> IsEnabledAsync() => ValueTask.FromResult(Status == MacOSLoginItemStatus.Enabled);

    /// <inheritdoc />
    public ValueTask SetAsync(bool on)
    {
        if (_service == IntPtr.Zero)
        {
            return ValueTask.CompletedTask;
        }

        // NULL for the NSError** out-parameter: the reason a registration was refused is not
        // something Altim acts on, and the caller reads the state back regardless.
        IntPtr selector = ObjC.Selector(on ? "registerAndReturnError:" : "unregisterAndReturnError:");
        _ = ObjC.SendBoolWithError(_service, selector, IntPtr.Zero);

        return ValueTask.CompletedTask;
    }
}
