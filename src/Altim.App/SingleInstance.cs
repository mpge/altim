using System.Diagnostics.CodeAnalysis;
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
/// Altim each. On Windows that is what an unprefixed name means; on macOS and Linux .NET
/// backs these primitives with per-user files, which gives the same property by a different
/// route.
/// </para>
/// <para>
/// <b>The two halves degrade independently.</b> The mutex is the guard and the event is only
/// how a second launch asks the first to show itself. A platform that will hand out one and
/// not the other gets the half it can: an Altim that is still single-instance but whose
/// second launch exits silently is a much smaller loss than two Altims.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Altim.SingleInstance";
    private const string SurfaceEventName = "Altim.Surface";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _surface;
    private readonly ManualResetEventSlim _stop = new(initialState: false);
    private readonly Thread? _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle? surface)
    {
        _mutex = mutex;
        _surface = surface;

        if (surface is null)
        {
            return;
        }

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
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException
                                      or PlatformNotSupportedException or WaitHandleCannotBeOpenedException)
        {
            mutex?.Dispose();
            AltimLog.Write("startup", "The single-instance guard could not be created; starting anyway", ex);
            return true;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing. The session is ours, and the
            // handle is ours too: an abandoned mutex is acquired by the waiter that
            // observes it.
            instance = null;
            return true;
        }

        // The session is held. Whether the surfacing channel can also be opened is a
        // separate question, and a failure there does not give the session back.
        instance = new SingleInstance(mutex, TryCreateSurfaceEvent());
        return true;
    }

    /// <summary>
    /// Asks the instance that already owns the session to show itself.
    /// </summary>
    /// <returns>True when the signal was delivered.</returns>
    public static bool SignalExisting()
    {
        try
        {
            if (TryOpenSurfaceEvent(out EventWaitHandle? surface))
            {
                using (surface)
                {
                    return surface.Set();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException
                                      or PlatformNotSupportedException or WaitHandleCannotBeOpenedException)
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
            _surface?.Set();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        _ = _listener?.Join(TimeSpan.FromSeconds(1));

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
        {
            // Not held, or already released. Either way the process is going away.
        }

        _surface?.Dispose();
        _mutex.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// Creates the event a second launch signals, or reports that this platform will not.
    /// </summary>
    /// <returns>The event, or <see langword="null"/> when none could be created.</returns>
    private static EventWaitHandle? TryCreateSurfaceEvent()
    {
        try
        {
            return new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SurfaceEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException
                                      or PlatformNotSupportedException or WaitHandleCannotBeOpenedException)
        {
            AltimLog.Write(
                "startup",
                "A second launch will not be able to surface this instance's panel; it will exit quietly",
                ex);
            return null;
        }
    }

    /// <summary>
    /// Opens the running instance's surfacing event.
    /// </summary>
    /// <param name="surface">The opened event, or null when there was none to open.</param>
    /// <returns>True when an existing event was opened.</returns>
    /// <remarks>
    /// <para>
    /// Two implementations, because .NET only offers <c>TryOpenExisting</c> on Windows. Off
    /// Windows the named primitive is create-or-attach in a single call and there is no way
    /// to ask for only the attach, so the constructor is used and <c>createdNew</c> is read
    /// back to tell the two apart.
    /// </para>
    /// <para>
    /// Creating one here means nobody was listening: the owner released the session between
    /// <see cref="TryAcquire"/> reporting it held and this call. That is reported as "no
    /// signal delivered" and the freshly created event is dropped rather than left behind
    /// for the next launch to attach to and find nobody on.
    /// </para>
    /// </remarks>
    private static bool TryOpenSurfaceEvent([NotNullWhen(true)] out EventWaitHandle? surface)
    {
        if (OperatingSystem.IsWindows())
        {
            return EventWaitHandle.TryOpenExisting(SurfaceEventName, out surface);
        }

        var opened = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, SurfaceEventName, out bool createdNew);

        if (createdNew)
        {
            opened.Dispose();
            surface = null;
            return false;
        }

        surface = opened;
        return true;
    }

    private void Listen()
    {
        if (_surface is null)
        {
            return;
        }

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
