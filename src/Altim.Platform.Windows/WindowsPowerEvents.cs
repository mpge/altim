#if WINDOWS

using Microsoft.Win32;

namespace Altim.Platform.Windows;

/// <summary>
/// Turns the Windows power broadcast into the single wake signal the scheduler
/// listens for. Compiled only into the Windows target framework, which is what lets
/// it call <see cref="SystemEvents"/> without a platform suppression.
/// </summary>
/// <remarks>
/// <see cref="SystemEvents"/> raises its events on a dedicated hidden window owned by
/// a thread the framework creates, so handlers do not run on the UI thread and
/// subscribers must marshal themselves.
/// </remarks>
public sealed class WindowsPowerEvents : IDisposable
{
    private bool _disposed;

    /// <summary>Starts listening for power mode changes.</summary>
    public WindowsPowerEvents() => SystemEvents.PowerModeChanged += OnPowerModeChanged;

    /// <summary>
    /// Raised when the machine resumes from sleep. Not raised for a suspend, and not
    /// raised for the status changes Windows sends when the battery state moves.
    /// </summary>
    public event EventHandler? SystemResumed;

    /// <summary>Stops listening. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            SystemResumed?.Invoke(this, EventArgs.Empty);
        }
    }
}

#endif
