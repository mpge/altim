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

    private readonly PopupWindow _window;
    private readonly PopupViewModel _viewModel;

    private Thickness? _chrome;
    private CoreRect? _lastAnchor;
    private PixelPoint? _lastCursor;
    private long _openedAt;
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

        AltimLog.Write("popup", "Hidden: " + reason);
        _window.Hide();
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

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // The panel is hidden, never closed, for the whole life of the process. Anything
        // that asks it to close before shutdown — alt+F4 on a focused panel, for instance —
        // gets a hide instead.
        if (_closing)
        {
            return;
        }

        e.Cancel = true;
        Close("close request");
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_disposed || !_window.IsVisible)
        {
            return;
        }

        // The click that opened the panel deactivates it a frame later on some transitions;
        // closing on that one turns an open into a flicker.
        if (Environment.TickCount64 - _openedAt < ReopenGuardMilliseconds)
        {
            return;
        }

#if WINDOWS
        // Clicking the tray icon makes the shell's tray window the foreground window, which
        // deactivates the panel. Treating that as a dismissal closes the panel a moment
        // before the same click reopens it, and the user sees a flicker instead of a toggle.
        if (WindowsShell.ForegroundBelongsToTrayOrSelf())
        {
            return;
        }
#endif

        Close("deactivated");
    }
}
