namespace Altim.Core.Tests.Fakes;

/// <summary>
/// A manually advanced clock with working timers.
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> is not on this machine and the build
/// takes no new packages, so this is the smallest thing that satisfies
/// <see cref="PeriodicTimer"/> and <see cref="TimeProvider.CreateTimer"/>.
/// </summary>
/// <remarks>
/// Callbacks run synchronously on the thread calling <see cref="Advance"/>, in due order,
/// with the clock set to each callback due time before it runs. Tests therefore advance
/// time and then wait on a signal from the code under test rather than assuming the work
/// finished inline.
/// </remarks>
public sealed class TestTimeProvider : TimeProvider
{
    private readonly List<TestTimer> _timers = [];
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public TestTimeProvider()
        : this(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public TestTimeProvider(DateTimeOffset start) => _now = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new TestTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward, firing every timer that comes due on the way.
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + delta;
        }

        while (true)
        {
            TestTimer? due = null;
            DateTimeOffset dueAt = default;
            lock (_gate)
            {
                foreach (TestTimer timer in _timers)
                {
                    if (timer.NextDue is { } next && next <= target && (due is null || next < dueAt))
                    {
                        due = timer;
                        dueAt = next;
                    }
                }

                if (due is null)
                {
                    _now = target;
                    return;
                }

                _now = dueAt;
            }

            due.Fire();
        }
    }

    private void Remove(TestTimer timer)
    {
        lock (_gate)
        {
            _ = _timers.Remove(timer);
        }
    }

    private sealed class TestTimer(TestTimeProvider provider, TimerCallback callback, object? state) : ITimer
    {
        private readonly TestTimeProvider _provider = provider;
        private readonly TimerCallback _callback = callback;
        private readonly object? _state = state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public DateTimeOffset? NextDue { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_provider._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                NextDue = dueTime == Timeout.InfiniteTimeSpan ? null : _provider._now + dueTime;
                return true;
            }
        }

        public void Fire()
        {
            lock (_provider._gate)
            {
                if (_disposed)
                {
                    return;
                }

                NextDue = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero
                    ? null
                    : _provider._now + _period;
            }

            _callback(_state);
        }

        public void Dispose()
        {
            lock (_provider._gate)
            {
                _disposed = true;
                NextDue = null;
            }

            _provider.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
