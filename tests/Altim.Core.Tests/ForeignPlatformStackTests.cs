using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Platform.Linux;
using Altim.Platform.MacOS;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// That the macOS and Linux halves of the composition root can be built at all, on a machine
/// that is neither.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this does and does not prove.</b> It does not prove the macOS or Linux platform
/// layers work — nothing available here can, and ARCHITECTURE.md records both as unverified.
/// What it proves is the one thing that is checkable from anywhere and is also the most
/// likely way the wiring breaks: <c>PlatformStack.CreateMacOS</c> and
/// <c>PlatformStack.CreateLinux</c> construct five services each, and a constructor that
/// throws on the wrong platform turns a degraded capability into a failure to start. Every
/// type in both layers is compiled into every build of the solution and is documented to be
/// inert rather than absent off its own operating system; this is the test of that claim,
/// and it exercises the same constructors in the same order the composition root does.
/// </para>
/// <para>
/// <c>Altim.App</c> is not referenced by any test project — nothing depends on the
/// composition root, which is what keeps the rest testable — so the sequence is written out
/// rather than called. It is deliberately the same sequence; a service added to one and not
/// the other is the failure mode this cannot catch.
/// </para>
/// </remarks>
public sealed class ForeignPlatformStackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMacOSStackConstructsAndReportsItselfUnavailable()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "This asserts the off-platform behaviour.");

        using var platform = new MacOSPlatformService("Altim");
        var notifications = new MacOSNotificationService();
        var autoStart = new MacOSAutoStartService();
        var processes = new MacOSProcessMonitor();
        using var motion = new MacOSMotionPreferenceService();

        Assert.False(platform.IsSupported);
        Assert.False(notifications.IsAvailable);
        Assert.NotNull(notifications.UnavailableReason);
        Assert.False(autoStart.IsSupported);
        Assert.NotNull(autoStart.UnavailableReason);

        // Unavailable is not broken: every call still answers, because the composition root
        // binds the interfaces either way and the rest of Altim calls them regardless.
        Assert.Null(await platform.GetTrayAnchorAsync());
        await notifications.ShowAsync(new Notification("Title", "Body", "claude", null), Ct);
        Assert.False(await autoStart.IsEnabledAsync());

        // Deliberately not asserted empty. The macOS process monitor reads the process table
        // through the BCL rather than through anything Apple-specific, so it is the one piece
        // of that layer that genuinely works here and will happily report this machine's own
        // agent processes. What matters for the composition root is that the scan answers
        // instead of throwing; which platform gets to call it is decided above it.
        Assert.NotNull(await processes.ScanAsync(Ct));

        // No NSWorkspace to ask, so no answer: unknown, and never "the user wants motion".
        Assert.False(motion.IsSupported);
        Assert.Equal(MotionPreference.Unknown, motion.Current);

        // The signals exist and simply never fire, which is what the contract asks of a
        // platform that cannot report them.
        platform.SystemSuspending += (_, _) => Assert.Fail("No macOS notification centre exists here.");
        platform.SystemResumed += (_, _) => Assert.Fail("No macOS notification centre exists here.");
        motion.Changed += (_, _) => Assert.Fail("No macOS notification centre exists here.");
    }

    [Fact]
    public async Task TheLinuxStackConstructsAndReportsItselfUnavailable()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "This asserts the off-platform behaviour.");

        var tray = new LinuxTrayHost("Altim");
        using var platform = new LinuxPlatformService(tray);
        using var notifications = new LinuxNotificationService("Altim", LinuxAutoStartService.DefaultEntryName);
        var autoStart = new LinuxAutoStartService();
        var processes = new LinuxProcessMonitor();
        using var motion = new LinuxMotionPreferenceService();

        // Nothing waits for these in the application; a test can, and does, so the assertions
        // below are about a settled state rather than a race.
        await platform.Ready;
        await motion.Ready;

        Assert.False(platform.SleepSignalConnected);
        Assert.False(platform.AppearancePortalConnected);

        // No session bus, so no portal, so no answer about motion. Unknown, not "animate".
        Assert.False(motion.PortalAnswered);
        Assert.Equal(MotionPreference.Unknown, motion.Current);

        // Null on Linux in every session, on every desktop: the StatusNotifierItem
        // specification carries no geometry. Null here is the answer, not a failure.
        Assert.Null(await platform.GetTrayAnchorAsync());

        await notifications.ShowAsync(new Notification("Title", "Body", "claude", null), Ct);
        Assert.Equal(LinuxNotificationDelivery.Unavailable, notifications.LastDelivery);
        Assert.NotNull(notifications.UnavailableReason);

        Assert.False(await autoStart.IsEnabledAsync());
        Assert.Empty(await processes.ScanAsync(Ct));
    }

    /// <summary>
    /// The composition root treats a Linux notification failure as a condition to report
    /// rather than something to probe for at start-up, so the failure has to be observable.
    /// </summary>
    [Fact]
    public async Task ALinuxNotificationThatIsNotDeliveredSaysSo()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "This asserts the off-platform behaviour.");

        using var notifications = new LinuxNotificationService("Altim", LinuxAutoStartService.DefaultEntryName);

        LinuxNotificationFailure? reported = null;
        notifications.DeliveryFailed += (_, failure) => reported = failure;

        await notifications.ShowAsync(new Notification("Title", "Body", "claude", null), Ct);

        Assert.NotNull(reported);
        Assert.False(string.IsNullOrWhiteSpace(reported.Reason));
    }
}
