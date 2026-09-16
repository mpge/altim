using Altim.UI.Views;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// What counts as the user clicking away from the tray panel.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN.md says the panel closes on deactivate. It did not, and the log says how badly:
/// across one whole application log the panel closed by deactivation <b>once</b>, against
/// fifty-six closes by clicking the tray icon a second time. The panel sat on top of
/// whatever the user switched to until they went back to the tray.
/// </para>
/// <para>
/// The cause was a guard that had to exist. Clicking the tray icon makes the shell's taskbar
/// the foreground window, which deactivates the panel; closing on that deactivation closes
/// the panel a moment before the same click reopens it, so the user asks for a toggle and
/// gets a flicker. The guard answered "not a real deactivation" to every foreground window
/// it could not identify — and no foreground window at all is what Windows reports while
/// activation is moving between two windows, which is exactly when this question is asked.
/// The exception swallowed the rule.
/// </para>
/// <para>
/// So the rule these assert is that an unidentifiable foreground is not an answer. Three
/// owners decide, and the fourth waits: <see cref="PopupDismissalDecision.Recheck"/> is the
/// only honest thing to do with a transition, and when the budget for asking runs out the
/// panel is dismissed rather than kept, because it is no longer the active window.
/// </para>
/// <para>
/// This is pure classification over three facts, so it needs no screen, no tray and no
/// operating system — the same reason <see cref="PopupPlacementTests"/> can assert a taskbar
/// on four edges without four displays.
/// </para>
/// </remarks>
public sealed class PopupDismissalTests
{
    /// <summary>The taskbar that owns the notification area: the click that toggles the panel.</summary>
    private const string Taskbar = "Shell_TrayWnd";

    /// <summary>The Windows 11 overflow flyout, where an unpromoted icon lives.</summary>
    private const string OverflowFlyout = "TopLevelWindowForOverflowXamlIsland";

    /// <summary>The Windows 10 overflow window, the same thing one generation earlier.</summary>
    private const string LegacyOverflow = "NotifyIconOverflowWindow";

    /// <summary>
    /// The regression. Windows clears the foreground window while activation moves between
    /// two windows and the deactivation arrives inside that gap, so "nothing owns the
    /// foreground" is the ordinary state of a genuine click-away — not evidence that the
    /// tray was clicked.
    /// </summary>
    [Fact]
    public void NoForegroundWindowIsATransitionRatherThanAVerdict()
    {
        PopupForegroundOwner owner = PopupDismissal.Classify(
            hasForegroundWindow: false, isOwnProcess: false, windowClass: default);

        Assert.Equal(PopupForegroundOwner.Unknown, owner);
        Assert.Equal(PopupDismissalDecision.Recheck, PopupDismissal.Decide(owner, canRecheck: true));
    }

    /// <summary>A window that will not name its class is worth exactly one more ask, too.</summary>
    [Fact]
    public void AnUnreadableWindowClassIsATransitionAsWell()
    {
        PopupForegroundOwner owner = PopupDismissal.Classify(
            hasForegroundWindow: true, isOwnProcess: false, windowClass: string.Empty);

        Assert.Equal(PopupForegroundOwner.Unknown, owner);
        Assert.Equal(PopupDismissalDecision.Recheck, PopupDismissal.Decide(owner, canRecheck: true));
    }

    /// <summary>
    /// And the waiting is bounded. A foreground that never settles dismisses the panel: the
    /// panel is not the active window, DESIGN.md says that closes it, and waiting forever for
    /// certainty is the defect this replaced.
    /// </summary>
    [Fact]
    public void AForegroundThatNeverSettlesDismissesRatherThanKeeps() =>
        Assert.Equal(
            PopupDismissalDecision.Dismiss,
            PopupDismissal.Decide(PopupForegroundOwner.Unknown, canRecheck: false));

    /// <summary>
    /// The tray still toggles. Each of these is a window the click that is about to close the
    /// panel can be sitting in, so the deactivation it causes is left alone and the toggle
    /// does the closing.
    /// </summary>
    /// <param name="windowClass">The foreground window's class.</param>
    [Theory]
    [InlineData(Taskbar)]
    [InlineData(OverflowFlyout)]
    [InlineData(LegacyOverflow)]
    public void TheTrayAndItsOverflowAreNotADismissal(string windowClass)
    {
        PopupForegroundOwner owner = PopupDismissal.Classify(
            hasForegroundWindow: true, isOwnProcess: false, windowClass);

        Assert.Equal(PopupForegroundOwner.Tray, owner);
        Assert.Equal(PopupDismissalDecision.Keep, PopupDismissal.Decide(owner, canRecheck: true));
        Assert.Equal(PopupDismissalDecision.Keep, PopupDismissal.Decide(owner, canRecheck: false));
    }

    /// <summary>
    /// Anything else dismisses, immediately and without a re-check. The desktop is in the
    /// list on purpose: clicking the wallpaper is clicking away, and its window classes are
    /// no more special than an editor's.
    /// </summary>
    /// <param name="windowClass">The foreground window's class.</param>
    [Theory]
    [InlineData("Progman")]                      // the desktop
    [InlineData("WorkerW")]                      // the desktop again, with a wallpaper host behind it
    [InlineData("CabinetWClass")]                // File Explorer
    [InlineData("Chrome_WidgetWin_1")]           // a browser, and most Electron applications
    [InlineData("Notepad")]
    public void AnybodyElsesWindowIsADismissal(string windowClass)
    {
        PopupForegroundOwner owner = PopupDismissal.Classify(
            hasForegroundWindow: true, isOwnProcess: false, windowClass);

        Assert.Equal(PopupForegroundOwner.Other, owner);
        Assert.Equal(PopupDismissalDecision.Dismiss, PopupDismissal.Decide(owner, canRecheck: true));
    }

    /// <summary>
    /// The two classes that used to be read as the tray and are not identification at all.
    /// <c>Windows.UI.Core.CoreWindow</c> is the class of every packaged application's window,
    /// so clicking Settings or Calculator read as clicking the tray and the panel stayed up
    /// over them. <c>XamlExplorerHostIslandWindow</c> hosts Task View and the widgets board
    /// as well as shell flyouts, and those are click-aways like any other.
    /// </summary>
    /// <param name="windowClass">The foreground window's class.</param>
    [Theory]
    [InlineData("Windows.UI.Core.CoreWindow")]
    [InlineData("XamlExplorerHostIslandWindow")]
    public void AShellShapedClassThatOtherApplicationsAlsoUseIsNotTheTray(string windowClass)
    {
        Assert.False(PopupDismissal.IsTrayWindowClass(windowClass));
        Assert.Equal(
            PopupForegroundOwner.Other,
            PopupDismissal.Classify(hasForegroundWindow: true, isOwnProcess: false, windowClass));
    }

    /// <summary>
    /// A taskbar on a second monitor carries no notification area, so it can never be the
    /// click that toggles the panel — it is somebody else's window like any other, and
    /// clicking it dismisses.
    /// </summary>
    [Fact]
    public void ATaskbarOnAnotherMonitorIsNotTheTray() =>
        Assert.Equal(
            PopupForegroundOwner.Other,
            PopupDismissal.Classify(hasForegroundWindow: true, isOwnProcess: false, "Shell_SecondaryTrayWnd"));

    /// <summary>
    /// An overlay of the panel's own that takes focus is not the user leaving: it is the
    /// panel, one window further in. Closing the panel underneath it would take the overlay
    /// with it.
    /// </summary>
    [Fact]
    public void AnOverlayThePanelOwnsIsNotADismissal()
    {
        PopupForegroundOwner owner = PopupDismissal.Classify(
            hasForegroundWindow: true,
            isOwnProcess: true,
            windowClass: string.Empty,
            isPanelOrItsOverlay: true);

        Assert.Equal(PopupForegroundOwner.Self, owner);
        Assert.Equal(PopupDismissalDecision.Keep, PopupDismissal.Decide(owner, canRecheck: false));
    }

    /// <summary>
    /// The dashboard is one of ours and is still a click-away. "Belongs to this process" was
    /// the whole test, so clicking the dashboard while the panel was open left a topmost
    /// panel sitting over the window the user had just switched to — and being Altim's own
    /// window makes that more obviously a defect, not less.
    /// </summary>
    [Fact]
    public void AnotherAltimWindowThatThePanelDoesNotOwnIsADismissal()
    {
        PopupForegroundOwner owner = PopupDismissal.Classify(
            hasForegroundWindow: true,
            isOwnProcess: true,
            windowClass: string.Empty,
            isPanelOrItsOverlay: false);

        Assert.Equal(PopupForegroundOwner.Application, owner);
        Assert.Equal(PopupDismissalDecision.Dismiss, PopupDismissal.Decide(owner, canRecheck: true));
        Assert.Equal(PopupDismissalDecision.Dismiss, PopupDismissal.Decide(owner, canRecheck: false));
    }

    /// <summary>
    /// The two own-process answers read differently in the log, because "another Altim
    /// window took the foreground" was the line printed while the panel stayed up over the
    /// dashboard and it is the line somebody will search for.
    /// </summary>
    [Fact]
    public void TheTwoOwnProcessAnswersAreDescribedDifferently() =>
        Assert.NotEqual(
            PopupDismissal.Describe(PopupForegroundOwner.Self),
            PopupDismissal.Describe(PopupForegroundOwner.Application));

    /// <summary>
    /// Our own process is asked about before the class name is, because an Avalonia overlay
    /// window carries a framework class rather than one of Altim's and the class alone would
    /// read it as somebody else.
    /// </summary>
    [Fact]
    public void OwnershipIsDecidedBeforeTheClassNameIs() =>
        Assert.Equal(
            PopupForegroundOwner.Self,
            PopupDismissal.Classify(hasForegroundWindow: true, isOwnProcess: true, "Chrome_WidgetWin_1"));

    /// <summary>Class names are compared exactly: no prefix, no case folding.</summary>
    /// <param name="windowClass">A near miss for a class that is the tray.</param>
    [Theory]
    [InlineData("shell_traywnd")]
    [InlineData("Shell_TrayWnd2")]
    [InlineData("NotShell_TrayWnd")]
    public void ANearMissIsNotTheTray(string windowClass) =>
        Assert.False(PopupDismissal.IsTrayWindowClass(windowClass));

    /// <summary>
    /// A click that lands outside the panel is described as a click rather than as a
    /// foreground change, because the case the click test exists for is the one where no
    /// foreground moved at all: the panel was surfaced over a window that already had it.
    /// </summary>
    /// <param name="owner">Who owns the window the click landed on.</param>
    [Theory]
    [InlineData(PopupForegroundOwner.Unknown)]
    [InlineData(PopupForegroundOwner.Self)]
    [InlineData(PopupForegroundOwner.Tray)]
    [InlineData(PopupForegroundOwner.Other)]
    [InlineData(PopupForegroundOwner.Application)]
    public void AClickIsDescribedAsAClick(PopupForegroundOwner owner)
    {
        string described = PopupDismissal.DescribeClick(owner);

        Assert.False(string.IsNullOrWhiteSpace(described));
        Assert.Contains("click", described, StringComparison.Ordinal);
        Assert.NotEqual(PopupDismissal.Describe(owner), described);
    }

    /// <summary>Every class the classifier accepts is one it reports as the tray.</summary>
    [Fact]
    public void TheDeclaredTrayClassesAllClassifyAsTheTray()
    {
        Assert.NotEmpty(PopupDismissal.TrayWindowClasses);

        foreach (string windowClass in PopupDismissal.TrayWindowClasses)
        {
            Assert.Equal(
                PopupForegroundOwner.Tray,
                PopupDismissal.Classify(hasForegroundWindow: true, isOwnProcess: false, windowClass));
        }
    }
}
