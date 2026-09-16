namespace Altim.Core.Monitoring;

/// <summary>
/// One level of indirection between whatever produces a hint and whichever
/// <see cref="MonitorScheduler"/> is current.
/// </summary>
/// <remarks>
/// <para>
/// The same shape, and for the same reason, as the forwarding network gate the providers
/// hold. A scheduler's cadences are fixed at construction, so changing the refresh interval
/// in Settings replaces the instance — and everything that captured the old one is then
/// holding a disposed object.
/// </para>
/// <para>
/// <b>That failure is silent, which is why it needs a type.</b> A hint delivered to a
/// disposed scheduler does not throw: it is documented not to, because a
/// <c>FileSystemWatcher</c> is still delivering events while the process closes its windows.
/// So the filesystem watchers went on hinting a scheduler that had stopped, the meter moved
/// only on the 60-second polling floor, and "event-driven first" quietly became false until
/// the next restart, with nothing logged and nothing on screen to say so.
/// </para>
/// <para>
/// A rebuild can also land in the middle of the first-readings pass, which is the one pass
/// that has to happen: it is the walk that gives the incremental cursors somewhere to start
/// from, and it is the expensive one. A pass already inside the old scheduler when it was
/// replaced reads nothing and reports nothing, so <see cref="RefreshAllAsync"/> notices the
/// rebuild and takes the reading again through the replacement.
/// </para>
/// </remarks>
public sealed class SchedulerHandle
{
    /// <summary>
    /// How many rebuilds one pass will follow before giving up.
    /// </summary>
    /// <remarks>
    /// A bound rather than a loop: a user holding the refresh-interval control could
    /// otherwise keep a start-up pass running for as long as they cared to, and the polling
    /// floor covers whatever the last attempt missed a minute later.
    /// </remarks>
    public const int MaxFollowedRebuilds = 3;

    private readonly object _gate = new();
    private MonitorScheduler? _current;
    private int _generation;

    /// <summary>The scheduler in force, or <see langword="null"/> before one exists.</summary>
    public MonitorScheduler? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Counts bindings. Two reads of this either side of a piece of work say whether the
    /// scheduler was replaced while it ran.
    /// </summary>
    public int Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>Points the handle at the live scheduler.</summary>
    /// <param name="scheduler">The scheduler now in force, or null while none exists.</param>
    public void Bind(MonitorScheduler? scheduler)
    {
        lock (_gate)
        {
            _current = scheduler;
            _generation++;
        }
    }

    /// <summary>
    /// Records a hint against the scheduler that is current.
    /// </summary>
    /// <param name="providerId">
    /// The provider the hint is about, or <see langword="null"/> for every provider.
    /// </param>
    /// <remarks>
    /// A hint with nothing bound is dropped rather than thrown: it arrives before the
    /// scheduler exists or after it has gone, and the polling floor covers both.
    /// </remarks>
    public void Hint(string? providerId = null) => Current?.Hint(providerId);

    /// <summary>
    /// Refreshes every provider once, following a rebuild that lands mid-pass.
    /// </summary>
    /// <param name="ct">Cancels the refresh.</param>
    /// <returns>
    /// A task completing when a pass has finished against a scheduler that was still current
    /// when it ended. Never faults: a provider failure is reported through the scheduler's
    /// own events.
    /// </returns>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < MaxFollowedRebuilds; attempt++)
        {
            MonitorScheduler? scheduler;
            int generation;
            lock (_gate)
            {
                scheduler = _current;
                generation = _generation;
            }

            if (scheduler is null)
            {
                return;
            }

            await scheduler.RefreshAllAsync(ct).ConfigureAwait(false);

            lock (_gate)
            {
                if (_generation == generation)
                {
                    return;
                }
            }

            // The scheduler was replaced while that pass was running, so the pass may have
            // gone to an instance that was already stopping — which answers with nothing
            // rather than with an error. Take the reading again through the replacement.
            ct.ThrowIfCancellationRequested();
        }
    }
}
