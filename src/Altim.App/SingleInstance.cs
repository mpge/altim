using Altim.App.Diagnostics;

namespace Altim.App;

/// <summary>
/// The guard that keeps one tray icon per user session.
/// </summary>
/// <remarks>
/// <para>
/// A tray utility that starts twice is not twice as useful: it is two icons, two schedulers
/// reading the same directories, and two writers on one SQLite file. Autostart plus a manual
/// launch is the ordinary way that happens, which is also why the storage migrations
/// re-check their version inside each transaction.
/// </para>
/// <para>
/// The second launch does not simply die quietly, because from the user's side they asked
/// for the application and nothing happened. It signals the first instance, which surfaces
/// its panel, and then exits.
/// </para>
/// <para>
/// Both names are session-local rather than global, so two users signed in at once get one
/// Altim each.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Altim.SingleInstance";
    private const string SurfaceEventName = "Altim.Surface";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _surface;
    private readonly ManualResetEventSlim _stop = new(initialState: false);
    private readonly Thread _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle surface)
    {
        _mutex = mutex;
        _surface = surface;

        _listener = new Thread(Listen)
        {
            IsBackground = true,
            Name = "Altim single instance",
        };

        _listener.Start();
    }

    /// <summary>
    /// Raised on a background thread when another launch asked this instance to show itself.
    /// </summary>
    public event EventHandler? SurfaceRequested;

    /// <summary>
    /// Takes ownership of the session, or reports that another instance already has it.
    /// </summary>
    /// <param name="instance">
    /// The owned guard when this process is the first, otherwise <see langword="null"/>.
    /// </param>
    /// <returns>True when this process should carry on starting.</returns>
    /// <remarks>
    /// A machine that will not give out named handles at all — a locked-down container, for
    /// instance — is treated as "this is the only instance", because refusing to start over
    /// a guard would be worse than the duplicate it is guarding against.
    /// </remarks>
    public static bool TryAcquire(out SingleInstance? instance)
    {
        instance = null;

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            var surface = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SurfaceEventName);
            instance = new SingleInstance(mutex, surface);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or WaitHandleCannotBeOpenedException)
        {
            mutex?.Dispose();
            AltimLog.Write("startup", "The single-instance guard could not be created; starting anyway", ex);
            return true;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing. The session is ours.
            instance = null;
            return true;
        }
    }

    /// <summary>
    /// Asks the instance that already owns the session to show itself.
    /// </summary>
    /// <returns>True when the signal was delivered.</returns>
    public static bool SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(SurfaceEventName, out EventWaitHandle? surface))
            {
                using (surface)
                {
                    return surface.Set();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            AltimLog.Write("startup", "Signalling the running instance failed", ex);
        }

        return false;
    }

    /// <summary>Releases the session.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();

        try
        {
            _surface.Set();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        _ = _listener.Join(TimeSpan.FromSeconds(1));

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
        {
            // Not held, or already released. Either way the process is going away.
        }

        _surface.Dispose();
        _mutex.Dispose();
        _stop.Dispose();
    }

    private void Listen()
    {
        WaitHandle[] handles = [_surface, _stop.WaitHandle];

        while (!_disposed)
        {
            int signalled;
            try
            {
                signalled = WaitHandle.WaitAny(handles);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AbandonedMutexException)
            {
                return;
            }

            if (signalled != 0 || _disposed)
            {
                return;
            }

            SurfaceRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
