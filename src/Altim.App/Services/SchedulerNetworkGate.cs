using Altim.Core.Abstractions;

namespace Altim.App.Services;

/// <summary>
/// The gate handed to every provider, forwarding to whichever
/// <c>MonitorScheduler.NetworkGate</c> is current.
/// </summary>
/// <remarks>
/// <para>
/// Providers take their gate once, in their constructor. The scheduler's own gate is created
/// with the scheduler and carries that scheduler's
/// <c>MonitorSchedulerOptions.NetworkMinimumInterval</c>, and the scheduler's cadences are
/// fixed at construction — so applying a changed refresh interval means building a new
/// scheduler, which would leave every provider holding the dead one's gate.
/// </para>
/// <para>
/// One level of indirection fixes it. Providers hold this; this holds the live scheduler's
/// gate. Nothing about the rate limiting changes: the call that reaches the network still
/// asks the scheduler's limiter, keyed by provider id, and still gets the scheduler's floor.
/// </para>
/// <para>
/// Before a scheduler exists the gate is closed rather than open. A provider refreshing
/// during start-up reads its local files and reports everything it can; the one network call
/// it skips is retried on the next tick, which is a second later at most.
/// </para>
/// </remarks>
internal sealed class SchedulerNetworkGate : IRefreshGate
{
    private volatile IRefreshGate? _inner;

    /// <summary>Points the gate at the live scheduler's limiter.</summary>
    /// <param name="gate">The scheduler's gate, or null while none exists.</param>
    public void Bind(IRefreshGate? gate) => _inner = gate;

    /// <inheritdoc />
    public bool TryAcquire(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _inner?.TryAcquire(key) ?? false;
    }

    /// <inheritdoc />
    public TimeSpan TimeUntilAvailable(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _inner?.TimeUntilAvailable(key) ?? TimeSpan.Zero;
    }
}
