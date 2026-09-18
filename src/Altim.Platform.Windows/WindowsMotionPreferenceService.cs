#if WINDOWS

using Altim.Core.Abstractions;
using Altim.Core.Accessibility;
using Altim.Core.Models;
using Altim.Platform.Windows.Interop;

namespace Altim.Platform.Windows;

/// <summary>
/// The Windows implementation of <see cref="IMotionPreferenceService"/>: the "Animation
/// effects" accessibility setting, read once and then only when Windows says it moved.
/// </summary>
/// <remarks>
/// <para>
/// <b>The value comes from <c>SPI_GETCLIENTAREAANIMATION</c>.</b> It is the parameter behind
/// Settings &gt; Accessibility &gt; Visual effects &gt; Animation effects, and it is the one
/// Windows itself consults before animating a window. A call that fails leaves the answer
/// <see cref="MotionPreference.Unknown"/> rather than guessing at the common case.
/// </para>
/// <para>
/// <b>Changes arrive as <c>WM_SETTINGCHANGE</c>, on the tray's hidden top-level window.</b>
/// That window already exists, already receives broadcasts, and is the reason
/// <see cref="Interop.TrayWindow"/> has two windows rather than one; a second message window
/// for this would be a second thing to create, own and tear down for a message the first one
/// is already being handed.
/// </para>
/// <para>
/// <b>Every <c>WM_SETTINGCHANGE</c> re-reads the parameter, rather than only the ones whose
/// <c>wParam</c> names it.</b> Windows documents the broadcast as carrying the
/// <c>SPI_</c> action that caused it, and in practice accessibility changes have also been
/// seen carrying a section name instead. Filtering on <c>wParam</c> would be accepted
/// silently and then simply never fire — the same class of failure as observing the wrong
/// notification centre on macOS — so the filter is on the answer instead:
/// <see cref="Changed"/> is raised only when the value actually differs. The read is one
/// user32 call on a message that arrives when a person changes a system setting, not on a
/// frame.
/// </para>
/// <para>
/// <b>Without a tray host it still answers.</b> A machine whose tray could not be created
/// has no window for the broadcast to land on, so the preference is read once at start-up
/// and never changes. That is the same shape of degradation as the rest of the platform
/// stack: the contract is satisfied, one capability is quietly absent.
/// </para>
/// </remarks>
public sealed class WindowsMotionPreferenceService : IMotionPreferenceService, IDisposable
{
    private readonly WindowsTrayHost? _tray;

    private volatile MotionPreference _current;
    private bool _disposed;

    /// <summary>
    /// Reads the preference and, when a tray host is supplied, starts watching for changes.
    /// </summary>
    /// <param name="tray">
    /// The tray host whose hidden top-level window receives broadcasts, or
    /// <see langword="null"/> on a machine that has no tray. The host is not owned: it is
    /// disposed by the platform service, and this class only subscribes to it.
    /// </param>
    public WindowsMotionPreferenceService(WindowsTrayHost? tray = null)
    {
        _current = Read();
        _tray = tray;

        if (tray is not null)
        {
            tray.Window.MessageReceived += OnWindowMessage;
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public MotionPreference Current => _current;

    /// <summary>Stops watching for changes. The tray host is not disposed here.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_tray is not null)
        {
            _tray.Window.MessageReceived -= OnWindowMessage;
        }
    }

    /// <summary>
    /// Asks Windows whether client area animations are wanted.
    /// </summary>
    /// <returns>
    /// <see cref="MotionPreference.Full"/> or <see cref="MotionPreference.Reduced"/> when the
    /// call answered, and <see cref="MotionPreference.Unknown"/> when it did not.
    /// </returns>
    private static unsafe MotionPreference Read()
    {
        try
        {
            // BOOL is four bytes, and SystemParametersInfo writes all four. A call that
            // returned false wrote nothing, so there is no answer rather than a false one.
            int enabled = 0;
            bool? animations = NativeMethods.SystemParametersInfoW(
                NativeMethods.SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0)
                ? enabled != 0
                : null;

            return MotionPolicy.FromAnimationsEnabled(animations);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A Windows without this parameter is not one Altim supports, but an unknown
            // costs an animation and a throw from a constructor costs the tray icon.
            return MotionPreference.Unknown;
        }
    }

    private void OnWindowMessage(WindowMessage message)
    {
        if (_disposed || message.Message != NativeMethods.WM_SETTINGCHANGE)
        {
            return;
        }

        MotionPreference preference = Read();
        if (preference == _current)
        {
            return;
        }

        _current = preference;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

#endif
