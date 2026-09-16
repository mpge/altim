namespace Altim.Core.Abstractions;

/// <summary>
/// A minimum gap between two calls that share a key. Providers are handed one of these so
/// that the call which actually reaches the network is the thing being rate limited,
/// rather than the refresh it happens to sit inside: a provider whose live quota call is
/// held back still reads its local files, and still reports everything it can.
/// </summary>
/// <remarks>
/// Implementations are safe for concurrent use. The scheduler owns the instance and shares
/// it, so two providers gating on the same key share one floor.
/// </remarks>
public interface IRefreshGate
{
    /// <summary>
    /// Takes a slot for <paramref name="key"/> if one is due.
    /// </summary>
    /// <param name="key">The key to gate on, normally a provider id. Compared ordinally.</param>
    /// <returns>
    /// True when the caller may make the call, in which case the slot is taken. False when
    /// the key ran too recently; the caller skips that one call and reports the rest of
    /// what it knows.
    /// </returns>
    bool TryAcquire(string key);

    /// <summary>
    /// How long until <paramref name="key"/> may run again, so that a "Retry" button can
    /// say when it will work instead of looking dead.
    /// </summary>
    /// <param name="key">The key to inspect.</param>
    /// <returns>
    /// <see cref="TimeSpan.Zero"/> when the key may run now, including when it has never
    /// run. Never negative.
    /// </returns>
    TimeSpan TimeUntilAvailable(string key);
}
