using Altim.Core.Abstractions;
using Altim.Core.Models;

namespace Altim.Core.Monitoring;

/// <summary>
/// The single timing authority. Providers never start their own timers, so the process has
/// one wake source, one place to back off, and one place where a provider failure is
/// contained.
/// </summary>
/// <remarks>
/// <para>
/// Four things can start a refresh: the <see cref="PeriodicTimer"/> floor, a debounced
/// filesystem hint, a resume from sleep, and an explicit call. All four funnel through the
/// same per-provider path, which holds a per-provider gate, so refreshes of one provider
/// never overlap no matter which of them fires. A refresh that finds the gate taken is
/// dropped rather than queued: the next tick is never far away, and a queue would turn a
/// slow provider into a backlog.
/// </para>
/// <para>
/// Threading: one long-running loop task owns the periodic timer; hints and resumes start
/// short background tasks. Nothing marshals to a UI thread, so
/// <see cref="UsageUpdated"/> is raised on whichever thread finished the refresh and
/// subscribers marshal themselves. Cancellation is one linked source created in
/// <see cref="Start"/> and cancelled in <see cref="StopAsync"/>; a cancelled refresh
/// raises no event, so shutdown never looks like a provider failure.
/// </para>
/// </remarks>
public sealed class MonitorScheduler : IAsyncDisposable
{
    private readonly List<ProviderRegistration> _registrations = [];
    private readonly HashSet<string> _hintedProviders = new(StringComparer.Ordinal);
    private readonly RefreshRateLimiter _networkLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private PeriodicTimer? _pollTimer;
    private ITimer? _hintTimer;
    private Task? _loop;
    private bool _hintWindowOpen;
    private bool _hintedAll;
    private bool _uiVisible;
    private bool _suspended;
    private bool _disposed;

    /// <summary>
    /// Creates a scheduler with no providers registered.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock every timer, debounce and rate limit is measured against. Never
    /// <see langword="null"/>; pass a fake one in tests.
    /// </param>
    /// <param name="options">
    /// Cadences to run at, or <see langword="null"/> for
    /// <see cref="MonitorSchedulerOptions.Default"/>.
    /// </param>
    public MonitorScheduler(TimeProvider timeProvider, MonitorSchedulerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        Options = options ?? MonitorSchedulerOptions.Default;
        _networkLimiter = new RefreshRateLimiter(timeProvider, Options.NetworkMinimumInterval);
    }

    /// <summary>
    /// Creates a scheduler and registers every provider in <paramref name="providers"/> as
    /// local-only.
    /// </summary>
    /// <param name="providers">
    /// The providers to monitor. A provider that reaches the network registers through
    /// <see cref="Register"/> instead, so it can be rate limited.
    /// </param>
    /// <param name="timeProvider">The clock. Never <see langword="null"/>.</param>
    /// <param name="options">Cadences, or <see langword="null"/> for the defaults.</param>
    public MonitorScheduler(
        IEnumerable<IUsageProvider> providers,
        TimeProvider timeProvider,
        MonitorSchedulerOptions? options = null)
        : this(timeProvider, options)
    {
        ArgumentNullException.ThrowIfNull(providers);

        foreach (IUsageProvider provider in providers)
        {
            Register(provider);
        }
    }

    /// <summary>
    /// Raised after a provider has been refreshed, carrying that provider reading. A
    /// failed refresh raises it too, with <see cref="ProviderStatus.Error"/> and a detail
    /// message, which is how a failure reaches the UI without becoming a crash.
    /// </summary>
    /// <remarks>
    /// Raised on a background thread. A subscriber that throws is contained: the
    /// exception is swallowed so one bad handler cannot stop monitoring. Provider owned
    /// <see cref="IUsageProvider.UsageChanged"/> events are deliberately not forwarded,
    /// so a reading is announced exactly once per refresh.
    /// </remarks>
    public event EventHandler<ProviderUsage>? UsageUpdated;

    /// <summary>The cadences this scheduler was created with. Never <see langword="null"/>.</summary>
    public MonitorSchedulerOptions Options { get; }

    /// <summary>
    /// The interval the poll timer currently runs at: the tightened one while a window is
    /// open, the relaxed one otherwise.
    /// </summary>
    public TimeSpan CurrentInterval
    {
        get
        {
            lock (_gate)
            {
                return CurrentIntervalLocked;
            }
        }
    }

    /// <summary>True between <see cref="Suspend"/> and <see cref="Resume"/>.</summary>
    public bool IsSuspended
    {
        get
        {
            lock (_gate)
            {
                return _suspended;
            }
        }
    }

    /// <summary>True while a popup or dashboard is open, which tightens the cadence.</summary>
    public bool IsUiVisible
    {
        get
        {
            lock (_gate)
            {
                return _uiVisible;
            }
        }
    }

    /// <summary>True between <see cref="Start"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loop is not null;
            }
        }
    }

    private TimeSpan CurrentIntervalLocked => _uiVisible ? Options.TightenedInterval : Options.RelaxedInterval;

    /// <summary>
    /// Adds a provider to the rotation. Safe to call before or after <see cref="Start"/>;
    /// a provider added while running joins from the next refresh. Registering the same
    /// provider instance twice is ignored.
    /// </summary>
    /// <param name="provider">The provider to monitor. Never <see langword="null"/>.</param>
    /// <param name="touchesNetwork">
    /// True when refreshing this provider can reach the network, which subjects it to
    /// <see cref="MonitorSchedulerOptions.NetworkMinimumInterval"/> on top of the cadence.
    /// </param>
    public void Register(IUsageProvider provider, bool touchesNetwork = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            foreach (ProviderRegistration existing in _registrations)
            {
                if (ReferenceEquals(existing.Provider, provider))
                {
                    return;
                }
            }

            _registrations.Add(new ProviderRegistration(provider, touchesNetwork));
        }
    }

    /// <summary>
    /// Starts the poll timer. Idempotent: calling it while running does nothing. Does not
    /// refresh immediately; callers that want a reading at start-up call
    /// <see cref="RefreshAllAsync"/> themselves, which keeps start-up cost visible at the
    /// call site.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancellationToken token;
        PeriodicTimer timer;
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            token = _cts.Token;
            timer = new PeriodicTimer(CurrentIntervalLocked, _timeProvider);
            _pollTimer = timer;
            _loop = Task.Run(() => RunAsync(timer, token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Tells the scheduler whether a window is on screen, which switches the poll timer
    /// between the tightened and relaxed cadences. The change applies from the next tick.
    /// </summary>
    /// <param name="visible">True while the popup or dashboard is open.</param>
    public void SetUiVisible(bool visible)
    {
        lock (_gate)
        {
            if (_uiVisible == visible)
            {
                return;
            }

            _uiVisible = visible;
            if (_pollTimer is not null && !_disposed)
            {
                _pollTimer.Period = CurrentIntervalLocked;
            }
        }
    }

    /// <summary>
    /// Records a filesystem hint. Hints are not data: they say something may have changed,
    /// and several arriving together produce at most one refresh. The first hint opens a
    /// <see cref="MonitorSchedulerOptions.HintDebounce"/> window and every hint inside it
    /// is absorbed, so a session writing continuously cannot starve the refresh nor
    /// trigger one per write.
    /// </summary>
    /// <param name="providerId">
    /// The provider the hint is about, or <see langword="null"/> when it is not known,
    /// which refreshes every provider.
    /// </param>
    public void Hint(string? providerId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_suspended)
            {
                // A suspended machine is not producing real filesystem activity worth
                // acting on, and Resume refreshes everything anyway.
                return;
            }

            if (providerId is null)
            {
                _hintedAll = true;
            }
            else
            {
                _hintedProviders.Add(providerId);
            }

            if (_hintWindowOpen)
            {
                return;
            }

            _hintTimer ??= _timeProvider.CreateTimer(
                OnHintWindowElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _hintWindowOpen = true;
            _hintTimer.Change(Options.HintDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Pauses scheduling for a machine going to sleep. Ticks and hints stop; work already
    /// in flight is left to finish or be cancelled by shutdown.
    /// </summary>
    public void Suspend()
    {
        lock (_gate)
        {
            _suspended = true;
            _hintedProviders.Clear();
            _hintedAll = false;
        }
    }

    /// <summary>
    /// Resumes scheduling after a wake and, when
    /// <see cref="MonitorSchedulerOptions.RefreshOnResume"/> is set, triggers exactly one
    /// refresh of every provider. Calling it while not suspended does nothing, so a
    /// platform that reports resume twice does not refresh twice.
    /// </summary>
    public void Resume()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (!_suspended)
            {
                return;
            }

            _suspended = false;
            if (_disposed || !Options.RefreshOnResume)
            {
                return;
            }

            token = _cts?.Token ?? CancellationToken.None;
        }

        QueueRefresh(null, token);
    }

    /// <summary>
    /// Refreshes every registered provider once, concurrently.
    /// </summary>
    /// <param name="ct">Cancels the refresh. A cancelled refresh raises no event.</param>
    /// <returns>
    /// A task completing when every provider has finished or been skipped. It never
    /// faults: a provider failure is reported through <see cref="UsageUpdated"/>.
    /// </returns>
    public ValueTask RefreshAllAsync(CancellationToken ct = default) =>
        new(RefreshSelectionAsync(null, ct));

    /// <summary>
    /// Refreshes one provider.
    /// </summary>
    /// <param name="providerId">
    /// The <see cref="IUsageProvider.Id"/> to refresh. An unregistered id does nothing.
    /// </param>
    /// <param name="ct">Cancels the refresh.</param>
    /// <returns>A task completing when the provider has finished or been skipped.</returns>
    public ValueTask RefreshAsync(string providerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        return new ValueTask(RefreshSelectionAsync([providerId], ct));
    }

    /// <summary>
    /// Stops the poll timer, cancels in-flight work and waits briefly for it to unwind.
    /// Idempotent, and leaves the scheduler restartable through <see cref="Start"/>.
    /// </summary>
    public async ValueTask StopAsync()
    {
        CancellationTokenSource? cts;
        PeriodicTimer? timer;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            timer = _pollTimer;
            _pollTimer = null;
            loop = _loop;
            _loop = null;
            _hintWindowOpen = false;
            _hintedProviders.Clear();
            _hintedAll = false;
            _hintTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop is cancelled, not failed.
            }
        }

        timer?.Dispose();
        await WaitForInFlightRefreshesAsync().ConfigureAwait(false);
        cts?.Dispose();
    }

    /// <summary>
    /// Stops the scheduler and releases its timers and gates. Safe to call more than once.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);

        ITimer? hintTimer;
        ProviderRegistration[] registrations;
        lock (_gate)
        {
            _disposed = true;
            hintTimer = _hintTimer;
            _hintTimer = null;
            registrations = [.. _registrations];
            _registrations.Clear();
        }

        if (hintTimer is not null)
        {
            await hintTimer.DisposeAsync().ConfigureAwait(false);
        }

        foreach (ProviderRegistration registration in registrations)
        {
            registration.Gate.Dispose();
        }
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_suspended || _disposed)
                    {
                        continue;
                    }
                }

                await RefreshSelectionAsync(null, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping is not a failure.
        }
        catch (ObjectDisposedException)
        {
            // The timer went away underneath us during shutdown.
        }
    }

    private void OnHintWindowElapsed(object? state)
    {
        string[] hinted;
        bool all;
        CancellationToken token;
        lock (_gate)
        {
            _hintWindowOpen = false;
            all = _hintedAll;
            _hintedAll = false;
            hinted = [.. _hintedProviders];
            _hintedProviders.Clear();

            if (_suspended || _disposed)
            {
                return;
            }

            token = _cts?.Token ?? CancellationToken.None;
        }

        if (all)
        {
            QueueRefresh(null, token);
        }
        else if (hinted.Length > 0)
        {
            QueueRefresh(hinted, token);
        }
    }

    private void QueueRefresh(IReadOnlyCollection<string>? providerIds, CancellationToken ct) =>
        _ = Task.Run(() => RefreshSelectionAsync(providerIds, ct), CancellationToken.None);

    private async Task RefreshSelectionAsync(IReadOnlyCollection<string>? providerIds, CancellationToken ct)
    {
        ProviderRegistration[] targets = Snapshot(providerIds);
        if (targets.Length == 0)
        {
            return;
        }

        if (targets.Length == 1)
        {
            await RefreshProviderAsync(targets[0], ct).ConfigureAwait(false);
            return;
        }

        var work = new Task[targets.Length];
        for (int i = 0; i < targets.Length; i++)
        {
            work[i] = RefreshProviderAsync(targets[i], ct);
        }

        await Task.WhenAll(work).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes one provider, contains its failures, and never throws: one provider
    /// falling over must not take the tick, the other providers, or the process with it.
    /// </summary>
    private async Task RefreshProviderAsync(ProviderRegistration registration, CancellationToken ct)
    {
        // Non-blocking: a refresh already in flight for this provider means this one is
        // dropped, which is what keeps refreshes of one provider from overlapping.
        if (!registration.TryEnter())
        {
            return;
        }

        try
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (registration.TouchesNetwork && !_networkLimiter.TryAcquire(registration.Provider.Id))
            {
                return;
            }

            ProviderUsage usage;
            try
            {
                await registration.Provider.RefreshAsync(ct).ConfigureAwait(false);
                usage = await registration.Provider.GetUsageAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                usage = Failed(registration.Provider.Id, ex);
            }

            RaiseUsageUpdated(usage);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced this refresh. Nothing to report.
        }
        catch (Exception)
        {
            // Defence in depth: a subscriber or a provider misbehaving off the paths above
            // still cannot break the loop that called us.
        }
        finally
        {
            registration.Exit();
        }
    }

    private ProviderUsage Failed(string providerId, Exception ex)
    {
        string detail = string.IsNullOrWhiteSpace(ex.Message) ? "Unable to retrieve usage" : ex.Message;
        return new ProviderUsage(providerId, ProviderStatus.Error, [], null, _timeProvider.GetUtcNow(), detail);
    }

    private void RaiseUsageUpdated(ProviderUsage usage) => UsageUpdated?.Invoke(this, usage);

    private ProviderRegistration[] Snapshot(IReadOnlyCollection<string>? providerIds)
    {
        lock (_gate)
        {
            if (_disposed || _registrations.Count == 0)
            {
                return [];
            }

            if (providerIds is null)
            {
                return [.. _registrations];
            }

            List<ProviderRegistration> selected = [];
            foreach (ProviderRegistration registration in _registrations)
            {
                foreach (string id in providerIds)
                {
                    if (string.Equals(registration.Provider.Id, id, StringComparison.Ordinal))
                    {
                        selected.Add(registration);
                        break;
                    }
                }
            }

            return [.. selected];
        }
    }

    private async Task WaitForInFlightRefreshesAsync()
    {
        ProviderRegistration[] registrations = Snapshot(null);
        foreach (ProviderRegistration registration in registrations)
        {
            // Bounded: shutdown waits for a well-behaved provider to unwind, and gives up
            // on one that ignores cancellation rather than hanging the process.
            if (await registration.Gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            {
                registration.Exit();
            }
        }
    }

    private sealed class ProviderRegistration(IUsageProvider provider, bool touchesNetwork)
    {
        public IUsageProvider Provider { get; } = provider;

        public bool TouchesNetwork { get; } = touchesNetwork;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool TryEnter()
        {
            try
            {
                return Gate.Wait(0);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Exit()
        {
            try
            {
                Gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed during shutdown; the count no longer matters.
            }
        }
    }
}
