using Altim.Core.Models;
using Altim.Platform.MacOS;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The macOS platform layer, run on macOS.
/// </summary>
/// <remarks>
/// <para>
/// This is the counterpart of <see cref="ForeignPlatformStackTests"/>, which asserts that the
/// same five services are inert on a machine that is not a Mac and is skipped on one that is.
/// Between them the two cover both halves of the claim <c>Altim.Platform.MacOS.csproj</c>
/// makes: that every type in it is compiled into every build and is inert off macOS, and that
/// on macOS it reaches the real Objective-C runtime.
/// </para>
/// <para>
/// <b>What this proves.</b> Constructing the stack sends real messages through the
/// <c>objc_msgSend</c> declarations in <c>Interop/ObjC.cs</c>: <c>objc_getClass</c>,
/// <c>sel_registerName</c>, <c>objc_allocateClassPair</c>, four <c>class_addMethod</c> calls,
/// <c>objc_registerClassPair</c>, and <c>alloc</c>/<c>init</c> on the class that results. A
/// mis-declared entry point does not fail quietly there; it faults, and a faulted test host is
/// a red build. It also proves the two guards that exist to stop a crash: an unbundled process
/// must not reach <c>UNUserNotificationCenter.currentNotificationCenter</c> or
/// <c>SMAppService</c>, because both raise an Objective-C exception that no managed
/// <c>catch</c> can take, and the test host is exactly such a process.
/// </para>
/// <para>
/// <b>What it does not prove.</b> Nothing here draws a menu bar item, shows a notification or
/// registers a login item. A runner has no user session to put a status item in, and an
/// unbundled process is refused the other two by design. Whether the menu bar presence works
/// is still unverified and <c>ARCHITECTURE.md</c> still says so.
/// </para>
/// <para>
/// <b>Why <c>GetTrayAnchorAsync</c> is not called.</b> It marshals to the Cocoa main thread
/// through <c>performSelectorOnMainThread:withObject:waitUntilDone:</c> and awaits the result.
/// Under Avalonia the main run loop is running and the work is drained; in a test host nothing
/// is running a run loop on the main thread, so the queued work never runs and the await never
/// completes. That is a property of the host rather than a defect in the service, but it does
/// mean the call hangs here rather than returning null, and adding it to this test would hang
/// the suite rather than fail it.
/// </para>
/// <para>
/// <b>Why this takes a few seconds.</b> Disposal is deliberately synchronous: an observer has
/// to be unregistered before the object it points at is released. Each of the three disposals
/// asks the main thread to do that and waits up to two seconds for a run loop that, for the
/// reason above, is not going to answer. Six seconds of waiting is the cost of that guard
/// being real, not a hang.
/// </para>
/// </remarks>
public sealed class MacOSNativeStackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMacOSStackReachesTheObjectiveCRuntimeAndRefusesToTouchWhatNeedsABundle()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "There is no Objective-C runtime to reach here.");

        // The same five services the composition root builds, in the same order.
        using var platform = new MacOSPlatformService("Altim");
        var notifications = new MacOSNotificationService();
        var autoStart = new MacOSAutoStartService();
        var processes = new MacOSProcessMonitor();
        using var motion = new MacOSMotionPreferenceService();

        // The callback class registered and an instance of it was allocated, which is every
        // libobjc entry point in Interop/ObjC.cs that does not need AppKit. Off macOS this is
        // false, and ForeignPlatformStackTests asserts that.
        Assert.True(
            platform.IsSupported,
            "The Objective-C runtime was not reachable, or the callback class would not register.");

        // The precondition the next four assertions are about. A test host is a bare
        // executable, never a .app, and that is the case the bundle guards exist for.
        Assert.False(MacOSAppBundle.LooksBundled(Environment.ProcessPath));

        // Not "notifications are switched off": not reached at all. An unbundled process that
        // asks UNUserNotificationCenter for the current centre is killed by an Objective-C
        // exception, so the only safe handling is to not ask, and this is the assertion that
        // the decision not to ask is actually being taken on a real Mac.
        Assert.False(notifications.IsAvailable);
        Assert.NotNull(notifications.UnavailableReason);

        Assert.False(autoStart.IsSupported);
        Assert.NotNull(autoStart.UnavailableReason);
        Assert.Equal(MacOSLoginItemStatus.Unavailable, autoStart.Status);

        // Unavailable is not broken. Every call still answers, because the composition root
        // binds these interfaces either way and the rest of Altim calls them regardless.
        MacOSNotificationFailure? reported = null;
        notifications.DeliveryFailed += (_, failure) => reported = failure;
        await notifications.ShowAsync(new Notification("Title", "Body", "claude", null), Ct);
        Assert.Equal(MacOSNotificationDelivery.Unavailable, notifications.LastDelivery);
        Assert.NotNull(reported);
        Assert.False(string.IsNullOrWhiteSpace(reported.Reason));

        Assert.False(await autoStart.IsEnabledAsync());
        await autoStart.SetAsync(true);
        Assert.False(await autoStart.IsEnabledAsync());

        // A real process table read, through the BCL rather than through anything Apple
        // specific. Not asserted empty: this machine may well be running an agent.
        Assert.NotNull(await processes.ScanAsync(Ct));

        // The value is whatever this machine is set to, and a runner is not guaranteed to have
        // an NSWorkspace to ask, so the answer is not asserted. What is asserted is that
        // asking produced one of the three answers rather than ending the process: reading the
        // preference sends respondsToSelector: and then, only if that said yes,
        // accessibilityDisplayShouldReduceMotion. Sending a selector an object does not
        // implement raises an Objective-C exception, so the guard being real is the test.
        Assert.True(Enum.IsDefined(motion.Current), "NSWorkspace produced a preference that is not one of the three.");
    }
}
