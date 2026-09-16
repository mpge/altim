namespace Altim.Core.Monitoring;

/// <summary>
/// A minimum gap between two refreshes that share a key, kept separately from the polling
/// cadence so that tightening the cadence for an open window cannot make a
/// network-touching call run faster than its floor. Safe for concurrent use.
/// </summary>
public sealed class RefreshRateLimiter
{
    private readonly Dictionary<string, DateTimeOffset> _lastAcquired = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    /// <summary>
    /// Creates a limiter measuring elapsed time with <paramref name="timeProvider"/>.
    /// </summary>
    /// <param name="timeProvider">The clock. Never <see langword="null"/>.</param>
    /// <param name="minimumInterval">
    /// The shortest permitted gap between two acquisitions of the same key. Zero or a
    /// negative value allows everything through.
    /// </param>
    public RefreshRateLimiter(TimeProvider timeProvider, TimeSpan minimumInterval)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        MinimumInterval = minimumInterval;
    }

    /// <summary>The shortest permitted gap between two acquisitions of the same key.</summary>
    public TimeSpan MinimumInterval { get; }

    /// <summary>
    /// Takes a slot for <paramref name="key"/> if one is due.
    /// </summary>
    /// <param name="key">The key to rate limit, normally a provider id. Compared ordinally.</param>
    /// <returns>
    /// True when the caller may proceed, in which case the current instant is recorded
    /// against the key. False when the key ran too recently; nothing is recorded and the
    /// caller skips the work rather than queueing it.
    /// </returns>
    public bool TryAcquire(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_lastAcquired.TryGetValue(key, out DateTimeOffset last) && now - last < MinimumInterval)
            {
                return false;
            }

            _lastAcquired[key] = now;
            return true;
        }
    }

    /// <summary>
    /// How long until <paramref name="key"/> may run again.
    /// </summary>
    /// <param name="key">The key to inspect.</param>
    /// <returns>
    /// <see cref="TimeSpan.Zero"/> when the key may run now, including when it has never
    /// run. Never negative.
    /// </returns>
    public TimeSpan TimeUntilAvailable(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_lastAcquired.TryGetValue(key, out DateTimeOffset last))
            {
                return TimeSpan.Zero;
            }

            TimeSpan elapsed = now - last;
            return elapsed >= MinimumInterval ? TimeSpan.Zero : MinimumInterval - elapsed;
        }
    }
}
