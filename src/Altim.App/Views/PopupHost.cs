using Altim.App.Diagnostics;
#if WINDOWS
using Altim.App.Interop;
#endif
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CoreRect = Altim.Core.Models.PixelRect;

namespace Altim.App.Views;

/// <summary>
/// The tray panel's window: created once at start-up, hidden rather than closed, and
/// positioned against the tray icon on every open.
/// </summary>
/// <remarks>
/// <para>
/// Keeping the window alive is not an optimisation, it is a requirement. A tray-only process
/// has no top level, and without one there is no <c>Screens</c> to ask for geometry; Windows
/// also only invalidates its cached screen list from a live window, so a process that closed
/// its last window opens the next one against monitors that may no longer be attached. The
/// window is therefore primed once, off screen and unactivated, and from then on it is shown
/// and hidden.
/// </para>
/// <para>
/// Priming also buys the open budget. The panel is laid out, its render surfaces exist and
/// its view model is already carrying the current readings, so an open is a position and a
/// show rather than a construction.
/// </para>
/// </remarks>
internal sealed class PopupHost : IDisposable
{
    /// <summary>The fallback inset if the design system's value cannot be resolved.</summary>
    private static readonly Thickness DefaultShadowInset = new(24, 16, 24, 32);

    /// <summary>Somewhere no monitor is, for the priming show.</summary>
    private static readonly PixelPoint OffScreen = new(-32000, -32000);

    /// <summary>
    /// A deactivation this soon after an open is the open's own focus transition rather than
    /// the user clicking elsewhere.
    /// </summary>
    private const long ReopenGuardMilliseconds = 250;

    /// <summary>
    /// How long to leave the foreground to settle before asking again who owns it.
    /// </summary>
    /// <remarks>
    /// Long enough that the tray icon's own click has been delivered and has closed the
    /// panel itself — the shell's three callbacks for one press arrive inside four
    /// milliseconds and the toggle is a dispatcher post behind them — and short enough that a
    /// dismissal still reads as immediate. It is also the design system's popup transition,
    /// which is the shortest interval this interface already asks anyone to notice.
    /// </remarks>
    private const int ForegroundRecheckMilliseconds = 120;

    /// <summary>
    /// How many times a deactivation may be re-read before it is taken at face value.
    /// </summary>
    /// <remarks>
    /// Two, so the answer has a quarter of a second to arrive. Past that the panel is hidden
    /// regardless: it is not the active window, and DESIGN.md says that closes it.
    /// </remarks>
    private const int ForegroundRecheckLimit = 2;

    /// <summary>
    /// How often the foreground is read while the panel is on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A deactivation is not guaranteed to arrive, and the panel is topmost.</b> Two
    /// reproduced ways to have a panel nobody can dismiss. A double click on the tray icon:
    /// the second click collapses the panel correctly, but the foreground goes to Explorer
    /// and the deactivation lands inside the reopen guard. And the second-launch surfacing:
    /// the running instance cannot take the foreground, so the panel opens without ever
    /// being active and there is no activation to lose. Either way the panel sits over the
    /// user's work and clicking away does nothing at all, because clicking away is only
    /// noticed through a deactivation that never came.
    /// </para>
    /// <para>
    /// So the panel watches instead of waiting, for two things: the foreground <em>changing</em>,
    /// and a click landing outside the panel — the second because the surfaced case moves no
    /// foreground at all, the window the click lands on having had it the whole time. A quarter
    /// of a second is below the threshold a dismissal reads as sluggish, the timer only runs
    /// while the panel is visible — seconds at a time — and a tick is a
    /// <c>GetForegroundWindow</c> and two <c>GetAsyncKeyState</c> reads, which together are
    /// cheaper than the frame the panel is already painting.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ForegroundWatchInterval = TimeSpan.FromMilliseconds(250);

    private readonly PopupWindow _window;
    private readonly PopupViewModel _viewModel;
    private readonly DispatcherTimer _foregroundWatch;

    private Thickness? _chrome;
    private IntPtr _foregroundBaseline;
    private CoreRect? _lastAnchor;
    private PixelPoint? _lastCursor;
    private long _openedAt;
    private int _showing;
    private bool _hiding;
    private bool _closing;
    private bool _disposed;

    /// <summary>Creates the host over a panel view model.</summary>
    /// <param name="viewModel">The panel to show.</param>
    public PopupHost(PopupViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        _window = new PopupWindow(viewModel);
        _window.Deactivated += OnDeactivated;
        _window.Closing += OnClosing;

        _foregroundWatch = new DispatcherTimer(
            ForegroundWatchInterval, DispatcherPriority.Background, OnForegroundWatchTick);
    }

    /// <summary>Raised after the panel has been shown.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised after the panel has been hidden.</summary>
    public event EventHandler? Closed;

    /// <summary>True while the panel is on screen.</summary>
    public bool IsOpen => _window.IsVisible;

    /// <summary>The live top level, which is what makes screen geometry reachable.</summary>
    public Window Window => _window;

    /// <summary>The panel's view model, which lives as long as the window does.</summary>
    public PopupViewModel ViewModel => _viewModel;

    /// <summary>
    /// Shows the window off screen once and hides it again, so that from here on it has a
    /// platform implementation, a measured size and a usable <c>Screens</c>.
    /// </summary>
    public void Prime()
    {
        try
        {
            _window.ShowActivated = false;
            _window.Position = OffScreen;
            _window.Show();
            _window.UpdateLayout();
            _window.Hide();
        }
        catch (Exception ex)
        {
            AltimLog.Write("popup", "Priming the panel window failed", ex);
        }
        finally
        {
            _window.ShowActivated = true;
        }
    }

    /// <summary>
    /// Shows the panel beside the tray icon, or beside the cursor, or in the working area's
    /// corner, whichever the caller could supply.
    /// </summary>
    /// <param name="anchor">The tray icon's rectangle, or null.</param>
    public void Open(CoreRect? anchor)
    {
        if (_disposed)
        {
            return;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        _lastAnchor = anchor;
        _lastCursor = ReadCursor();
        _openedAt = Environment.TickCount64;

        // A new showing. Any re-check still queued from the last one is about a panel that
        // is no longer on screen, and must not be allowed to hide this one.
        _showing++;

        try
        {
            // Twice on purpose. Once with the size from the previous open, so the window
            // never appears at a stale position; then again after Show has run a layout
            // pass, because SizeToContent means the height depends on how many providers
            // reported and that is not known until the panel has measured.
            Place();
            _window.Show();
            _window.UpdateLayout();
            Place();
            _window.Activate();
        }
        catch (Exception ex)
        {
            AltimLog.Write("popup", "Opening the panel failed", ex);
            return;
        }

        // Watched from here rather than from the first deactivation, because a panel that
        // never became the active window never gets one. The baseline is whatever holds the
        // foreground now — the panel itself on an ordinary open, and somebody else's window
        // when a second launch could not hand the right over — because what dismisses the
        // panel is the foreground moving, not who happens to hold it at this instant.
        _foregroundBaseline = ReadForegroundWindow();
        ForgetPendingClicks();
        _foregroundWatch.Start();

        AltimLog.Timing("popup open", System.Diagnostics.Stopwatch.GetElapsedTime(started));
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hides the panel. Never closes it.</summary>
    /// <param name="reason">Why, for the log. Dismissals are the hardest thing here to debug.</param>
    public void Close(string reason = "request")
    {
        if (_disposed || !_window.IsVisible)
        {
            return;
        }

        _showing++;
        _foregroundWatch.Stop();
        AltimLog.Write("popup", "Hidden: " + reason);

        // Hiding deactivates the window, and the deactivation arrives before IsVisible
        // has caught up — so without this the panel reports why it was hidden and then
        // reports deciding not to hide it.
        _hiding = true;
        try
        {
            _window.Hide();
        }
        finally
        {
            _hiding = false;
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the panel when it is hidden and hides it when it is shown.</summary>
    /// <param name="anchor">The tray icon's rectangle, or null.</param>
    public void Toggle(CoreRect? anchor)
    {
        if (IsOpen)
        {
            Close("toggled");
        }
        else
        {
            Open(anchor);
        }
    }

    /// <summary>Closes the window for real and releases the view model.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closing = true;

        _foregroundWatch.Stop();
        _window.Deactivated -= OnDeactivated;
        _window.Closing -= OnClosing;

        try
        {
            _window.Close();
        }
        catch (Exception ex)
        {
            AltimLog.Write("popup", "Closing the panel window failed", ex);
        }

        _viewModel.Dispose();
    }

    private static PixelPoint? ReadCursor()
    {
#if WINDOWS
        return WindowsShell.CursorPosition();
#else
        return null;
#endif
    }

    private void Place()
    {
        Screen? screen = PickScreen();
        PixelRect workingArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        double scaling = screen?.Scaling ?? _window.RenderScaling;

        Size size = _window.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            size = new Size(_window.Width, _window.Height);
        }

        // The window is the panel plus whichever inset it is currently carrying, so the
        // panel is what is left when that one is taken back off — not the design's, which
        // is a different number on any open that trimmed it.
        Thickness design = ShadowInset();
        Thickness carried = _chrome ?? design;
        var panel = new Size(
            Math.Max(1d, size.Width - carried.Left - carried.Right),
            Math.Max(1d, size.Height - carried.Top - carried.Bottom));

        PopupPlacementResult placed = PopupPlacement.Compute(
            _lastAnchor, _lastCursor, workingArea, scaling, panel, design);

        Reserve(placed.ShadowInset, panel.Width);
        _window.Position = placed.Position;
    }

    /// <summary>
    /// Gives the window the transparent room the placement assumed it had.
    /// </summary>
    /// <remarks>
    /// Placement trims the inset on the edge facing the tray icon, because that room is
    /// still window and a window over a tray icon swallows the clicks meant for it. The
    /// window has to be told, twice over: the panel's margin is the inset, and the window's
    /// width is the panel plus the inset either side — <c>SizeToContent</c> handles the
    /// height but the width is the theme's, and a window sized for one inset while its panel
    /// carries another centres the panel inside the difference.
    /// </remarks>
    /// <param name="inset">The inset placement computed with.</param>
    /// <param name="panelWidth">The panel's width in device-independent units.</param>
    private void Reserve(Thickness inset, double panelWidth)
    {
        if (_chrome == inset)
        {
            return;
        }

        _chrome = inset;
        PopupChrome.SetShadowInset(_window, inset);
        _window.Width = panelWidth + inset.Left + inset.Right;
    }

    private Screen? PickScreen()
    {
        Screens screens = _window.Screens;

        PixelRect target = PopupPlacement.Resolve(_lastAnchor, _lastCursor);
        if (target.Width > 0 && target.Height > 0)
        {
            Screen? found = screens.ScreenFromPoint(new PixelPoint(
                target.X + (target.Width / 2),
                target.Y + (target.Height / 2)));

            if (found is not null)
            {
                return found;
            }
        }

        return screens.ScreenFromWindow(_window) ?? screens.Primary;
    }

    private Thickness ShadowInset()
    {
        ThemeVariant variant = _window.ActualThemeVariant;
        if (_window.TryGetResource("AltimPopupShadowMargin", variant, out object? value) && value is Thickness inset)
        {
            return inset;
        }

        return DefaultShadowInset;
    }

    /// <summary>
    /// Refuses a close and hides the panel instead — except the one caller that must never
    /// be refused.
    /// </summary>
    /// <remarks>
    /// Avalonia asks every window to close while it is answering the operating system's
    /// session-end query, and a cancelled close is the answer it hands back. A panel that
    /// cancelled unconditionally therefore vetoed <c>WM_QUERYENDSESSION</c>, and the user got
    /// the shell's "this app is preventing you from signing out" screen with Altim named on
    /// it. The rule is <see cref="PopupCloseRule"/>, which is a pure function over the reason
    /// and is asserted without a window.
    /// </remarks>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (PopupCloseRule.Decide(e.CloseReason) is PopupCloseAction.Close)
        {
            // Let it through, and stop pretending the window is coming back.
            _closing = true;
            _foregroundWatch.Stop();
            AltimLog.Write("popup", "Close allowed: " + PopupCloseRule.Describe(e.CloseReason));
            return;
        }

        e.Cancel = true;
        Close("close request");
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_disposed || _hiding || !_window.IsVisible)
        {
            return;
        }

        // The click that opened the panel deactivates it a frame later on some transitions;
        // closing on that one turns an open into a flicker. But a deactivation inside the
        // guard is not nothing, and swallowing it outright is how a double click on the tray
        // icon left a panel that click-away could not dismiss: the second click collapsed the
        // panel, the foreground went to Explorer, and the deactivation that said so arrived
        // inside the guard and was dropped. So the question is asked again once the guard is
        // over rather than answered "keep".
        long sinceOpen = Environment.TickCount64 - _openedAt;
        if (sinceOpen < ReopenGuardMilliseconds)
        {
            int showing = _showing;
            DispatcherTimer.RunOnce(
                () => Resolve(ForegroundRecheckLimit, showing),
                TimeSpan.FromMilliseconds(ReopenGuardMilliseconds - sinceOpen),
                DispatcherPriority.Input);
            return;
        }

        Resolve(ForegroundRecheckLimit, _showing);
    }

    /// <summary>
    /// Reads who owns the foreground while the panel is on screen, so a dismissal does not
    /// depend on a deactivation arriving.
    /// </summary>
    private void OnForegroundWatchTick(object? sender, EventArgs e)
    {
        if (_disposed || _hiding || !_window.IsVisible)
        {
            _foregroundWatch.Stop();
            return;
        }

        if (Environment.TickCount64 - _openedAt < ReopenGuardMilliseconds)
        {
            // The open's own focus transition. The deactivation path already schedules a
            // re-check for the moment the guard is over.
            return;
        }

        IntPtr foreground = ReadForegroundWindow();
        if (foreground == _foregroundBaseline)
        {
            // Nothing has moved since the last look. That is the panel surfaced over a
            // window which already held the foreground: the user asked for it and nothing
            // has happened since, so it stays — but a click into that window is something
            // happening, and it moves no foreground at all, so it is asked about directly.
            PopupForegroundOwner clicked = ReadClickedOwner();
            if (clicked is not PopupForegroundOwner.Unknown
                && PopupDismissal.Decide(clicked, canRecheck: false) is PopupDismissalDecision.Dismiss)
            {
                Close("clicked away, " + PopupDismissal.DescribeClick(clicked));
            }

            return;
        }

        PopupForegroundOwner owner = ClassifyForeground(foreground);
        if (owner is PopupForegroundOwner.Unknown)
        {
            // Activation is still moving. Not a new baseline and not a verdict; the next
            // tick asks again.
            return;
        }

        if (PopupDismissal.Decide(owner, canRecheck: false) is not PopupDismissalDecision.Dismiss)
        {
            // Ours or the tray. The foreground has moved, so this is the position the next
            // move is measured against.
            _foregroundBaseline = foreground;
            return;
        }

        Close("foreground moved, " + PopupDismissal.Describe(owner));
    }

    /// <summary>
    /// Decides what a deactivation was, re-reading the foreground rather than guessing at it
    /// while activation is still moving.
    /// </summary>
    /// <param name="rechecksLeft">How many more times the question may be asked.</param>
    /// <param name="showing">
    /// The showing this deactivation belonged to. A re-check that arrives after the panel has
    /// been hidden and shown again is about a window that is no longer the one on screen.
    /// </param>
    /// <remarks>
    /// Waiting costs nothing in the case the guard exists for. Clicking the tray icon
    /// deactivates the panel and then toggles it shut a few milliseconds later, so by the
    /// time the first re-check runs the panel is already hidden and this returns at the
    /// visibility check — and if it is somehow not, the foreground has settled on the tray by
    /// then and the answer is to keep it open anyway.
    /// </remarks>
    private void Resolve(int rechecksLeft, int showing)
    {
        if (_disposed || !_window.IsVisible || showing != _showing)
        {
            return;
        }

        PopupForegroundOwner owner = ReadForegroundOwner();

        switch (PopupDismissal.Decide(owner, canRecheck: rechecksLeft > 0))
        {
            case PopupDismissalDecision.Keep:
                AltimLog.Write("popup", "Deactivation ignored: " + PopupDismissal.Describe(owner));
                return;

            case PopupDismissalDecision.Recheck:
                DispatcherTimer.RunOnce(
                    () => Resolve(rechecksLeft - 1, showing),
                    TimeSpan.FromMilliseconds(ForegroundRecheckMilliseconds),
                    DispatcherPriority.Input);
                return;

            default:
                Close("deactivated, " + PopupDismissal.Describe(owner));
                return;
        }
    }

    /// <summary>
    /// Who took the foreground, where the question can be asked.
    /// </summary>
    /// <remarks>
    /// Only Windows both raises this deactivation for its own tray and can name the window
    /// that caused it. Everywhere else a deactivation is reported as what it says it is,
    /// which is the behaviour the panel had on those platforms before any of this existed.
    /// </remarks>
    private PopupForegroundOwner ReadForegroundOwner()
    {
#if WINDOWS
        // The panel's own handle goes with the question, because "belongs to this process"
        // is two different answers: an overlay the panel owns is the panel, and the
        // dashboard is a window the user has switched to.
        return WindowsShell.ForegroundOwner(PanelHandle());
#else
        return PopupForegroundOwner.Other;
#endif
    }

    /// <summary>The window that holds the foreground, as a handle, or zero off Windows.</summary>
    /// <remarks>
    /// Zero everywhere else, which is what turns the watch off on those platforms: the
    /// baseline never changes, so it never decides anything, and a deactivation is reported
    /// as what it says it is — the behaviour the panel had there before any of this existed.
    /// </remarks>
    private static IntPtr ReadForegroundWindow()
    {
#if WINDOWS
        return WindowsShell.ForegroundWindow();
#else
        return IntPtr.Zero;
#endif
    }

    /// <summary>
    /// Who owns the window a click has landed on since the last look, or
    /// <see cref="PopupForegroundOwner.Unknown"/> when there has been no click.
    /// </summary>
    private PopupForegroundOwner ReadClickedOwner()
    {
#if WINDOWS
        return WindowsShell.ClickedOn(PanelHandle());
#else
        return PopupForegroundOwner.Unknown;
#endif
    }

    /// <summary>Discards clicks that happened before the panel was on screen.</summary>
    private static void ForgetPendingClicks()
    {
#if WINDOWS
        WindowsShell.ForgetPendingClicks();
#endif
    }

    /// <summary>Who owns a window the watch has just noticed taking the foreground.</summary>
    /// <param name="foreground">The window that now holds it.</param>
    private PopupForegroundOwner ClassifyForeground(IntPtr foreground)
    {
#if WINDOWS
        return WindowsShell.ClassifyForeground(foreground, PanelHandle());
#else
        _ = foreground;
        return PopupForegroundOwner.Unknown;
#endif
    }

#if WINDOWS
    /// <summary>The panel window's native handle, or zero before it has one.</summary>
    private IntPtr PanelHandle() => _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
#endif
}
