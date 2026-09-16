using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Usage;

namespace Altim.Core.Monitoring;

/// <summary>
/// The single timing authority. Providers never start their own timers, so the process has
/// one wake source, one place to back off, and one place where a provider failure is
/// contained.
/// </summary>
/// <remarks>
/// <para>
/// Five things can start a refresh: the <see cref="PeriodicTimer"/> floor, a debounced
/// filesystem hint, a push from a provider's own watcher, a resume from sleep, and an
/// explicit call. All five funnel through the same per-provider path, which holds a
/// per-provider gate, so refreshes of one provider never overlap no matter which of them
/// fires. A refresh that finds the gate taken is not dropped: it marks the registration
/// dirty and the in-flight refresh runs once more as it leaves, so a change that landed
/// while a read was in progress is not hidden until the next relaxed tick.
/// </para>
/// <para>
/// The tick queues its refreshes rather than awaiting them. One provider taking a second
/// to answer must not push every other provider's cadence out behind it, and the loop must
/// stay free to unwind the moment shutdown asks it to. Each provider call is bounded by
/// <see cref="MonitorSchedulerOptions.ProviderTimeout"/>: a provider that ignores
/// cancellation becomes an error reading instead of a gate held for the life of the
/// process.
/// </para>
/// <para>
/// Threading: one long-running loop task owns the periodic timer; hints, pushes and
/// resumes start short background tasks. Nothing marshals to a UI thread, so
/// <see cref="UsageUpdated"/> is raised on whichever thread finished the refresh and
/// subscribers marshal themselves. Cancellation is one linked source created in
/// <see cref="Start"/> and cancelled in <see cref="StopAsync"/>; a cancelled refresh
/// raises no event, so shutdown never looks like a provider failure, and work that finds
/// no live source bails out rather than running uncancellable.
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
    /// Creates a scheduler and registers every provider in <paramref name="providers"/>.
    /// </summary>
    /// <param name="providers">The providers to monitor.</param>
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
    /// Raised after a provider has been refreshed, carrying that provider reading already
    /// normalised: percentages clamped, and a metric whose window has reset reported as
    /// unavailable rather than as a frozen number. A failed refresh raises it too, with
    /// <see cref="ProviderStatus.Error"/> and the one line the design allows, which is how
    /// a failure reaches the UI without becoming a crash.
    /// </summary>
    /// <remarks>
    /// Raised on a background thread. Subscribers are invoked one at a time and a
    /// subscriber that throws is contained, so one bad handler can neither stop monitoring
    /// nor starve the handlers behind it. Provider owned
    /// <see cref="IUsageProvider.UsageChanged"/> events are not forwarded as readings:
    /// they are taken as hints, so a reading is announced exactly once per refresh.
    /// </remarks>
    public event EventHandler<ProviderUsage>? UsageUpdated;

    /// <summary>
    /// Raised when a provider refresh fails, carrying the exception for logging. The
    /// reading raised through <see cref="UsageUpdated"/> for the same failure carries the
    /// contract copy instead, because exception text names file paths.
    /// </summary>
    public event EventHandler<ProviderFailure>? ProviderFailed;

    /// <summary>The cadences this scheduler was created with. Never <see langword="null"/>.</summary>
    public MonitorSchedulerOptions Options { get; }

    /// <summary>
    /// The gate network-touching provider calls run behind, keyed by provider id and
    /// floored at <see cref="MonitorSchedulerOptions.NetworkMinimumInterval"/>. Hand it to
    /// providers that make such a call: the scheduler refreshes every provider on every
    /// tick, and it is the call that reaches the network which is rate limited, never the
    /// whole read. <see cref="IRefreshGate.TimeUntilAvailable"/> is what lets a "Retry"
    /// button say when it will work instead of looking dead.
    /// </summary>
    public IRefreshGate NetworkGate => _networkLimiter;

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
    /// Adds a provider to the rotation and subscribes to its
    /// <see cref="IUsageProvider.UsageChanged"/> event, which is taken as a hint rather
    /// than as a reading. Safe to call before or after <see cref="Start"/>; a provider
    /// added while running joins from the next refresh. Registering the same provider
    /// instance twice is ignored.
    /// </summary>
    /// <param name="provider">The provider to monitor. Never <see langword="null"/>.</param>
    public void Register(IUsageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ProviderRegistration registration;
        lock (_gate)
        {
            foreach (ProviderRegistration existing in _registrations)
            {
                if (ReferenceEquals(existing.Provider, provider))
                {
                    return;
                }
            }

            registration = new ProviderRegistration(provider);
            registration.Push = (_, _) => OnProviderPush(registration);
            _registrations.Add(registration);
        }

        provider.UsageChanged += registration.Push;
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
    /// Records a hint. Hints are not data: they say something may have changed, and
    /// several arriving together produce at most one refresh. The first hint opens a
    /// <see cref="MonitorSchedulerOptions.HintDebounce"/> window and every hint inside it
    /// is absorbed, so a session writing continuously cannot starve the refresh nor
    /// trigger one per write.
    /// </summary>
    /// <param name="providerId">
    /// The provider the hint is about, or <see langword="null"/> when it is not known,
    /// which refreshes every provider.
    /// </param>
    /// <remarks>
    /// A hint after disposal does nothing. A filesystem watcher is still delivering events
    /// while the process closes its windows, and a late event is not a programming error
    /// worth throwing at a thread-pool thread over. <see cref="Register"/> and
    /// <see cref="Start"/> still throw, because those are calls nobody makes by accident.
    /// </remarks>
    public void Hint(string? providerId = null)
    {
        lock (_gate)
        {
            if (_disposed || _suspended)
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
                _ = _hintedProviders.Add(providerId);
            }

            if (_hintWindowOpen)
            {
                return;
            }

            _hintTimer ??= _timeProvider.CreateTimer(
                OnHintWindowElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _hintWindowOpen = true;
            _ = _hintTimer.Change(Options.HintDebounce, Timeout.InfiniteTimeSpan);
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
    /// platform that reports resume twice does not refresh twice, and calling it on a
    /// scheduler that is not running does nothing either: there is no run for that refresh
    /// to belong to and nothing that could cancel it.
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
            if (!Options.RefreshOnResume || !TryGetRunTokenLocked(out token))
            {
                return;
            }
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
    /// <remarks>
    /// Every wait here is bounded by
    /// <see cref="MonitorSchedulerOptions.ShutdownTimeout"/>. A provider that ignores its
    /// cancellation token is left running rather than allowed to hold the process open:
    /// closing a tray utility must never depend on a third-party CLI answering.
    /// </remarks>
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
            _ = _hintTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(Options.ShutdownTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop is cancelled, not failed.
            }
            catch (TimeoutException)
            {
                // The loop is wedged behind something that will not unwind. Shutdown
                // carries on; the process is going away regardless.
            }
        }

        timer?.Dispose();
        await WaitForInFlightRefreshesAsync().ConfigureAwait(false);
        cts?.Dispose();
    }

    /// <summary>
    /// Stops the scheduler, unsubscribes from every provider and releases its timers and
    /// gates. Safe to call more than once.
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
            if (registration.Push is { } push)
            {
                registration.Provider.UsageChanged -= push;
            }

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

                // Queued rather than awaited: the loop owns the cadence, not the slowest
                // provider on it, and shutdown has to be able to unwind this immediately.
                QueueRefresh(null, ct);
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

            if (_suspended || !TryGetRunTokenLocked(out token))
            {
                return;
            }
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

    /// <summary>
    /// A provider's own watcher noticed something. It is a hint, not a reading: the
    /// refresh it triggers is debounced like any other, and it is dropped outright while
    /// this provider is already being refreshed, because that refresh is about to announce
    /// the same reading.
    /// </summary>
    private void OnProviderPush(ProviderRegistration registration)
    {
        if (registration.IsRefreshing)
        {
            return;
        }

        Hint(registration.Provider.Id);
    }

    /// <summary>
    /// The token in-flight work runs under, or false when there is no run to attach work
    /// to. Substituting <see cref="CancellationToken.None"/> here would start work after
    /// shutdown that nothing could ever cancel. Called under <see cref="_gate"/>.
    /// </summary>
    private bool TryGetRunTokenLocked(out CancellationToken token)
    {
        token = default;
        if (_disposed || _cts is null)
        {
            return false;
        }

        try
        {
            token = _cts.Token;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
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
        // recorded rather than run, and the one in flight picks it up as it leaves. That
        // is what keeps refreshes of one provider from overlapping without losing the
        // change that asked for this one.
        if (!registration.TryEnter())
        {
            registration.MarkDirty();
            return;
        }

        bool gateHandedOff = false;
        try
        {
            registration.BeginRefresh();
            while (true)
            {
                registration.ClearDirty();
                gateHandedOff = await RefreshOnceAsync(registration, ct).ConfigureAwait(false);

                if (gateHandedOff || !registration.IsDirty || ct.IsCancellationRequested || _disposed)
                {
                    break;
                }
            }
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
            registration.EndRefresh();
            if (!gateHandedOff)
            {
                registration.Exit();
            }
        }
    }

    /// <summary>
    /// One read of one provider, bounded by
    /// <see cref="MonitorSchedulerOptions.ProviderTimeout"/>.
    /// </summary>
    /// <returns>
    /// True when the provider outlasted its budget and the gate was handed to the
    /// abandoned call, which releases it if and when the provider ever returns. The
    /// scheduler does not wait for that, and does not call into the provider again while
    /// it is still inside a call.
    /// </returns>
    private async Task<bool> RefreshOnceAsync(ProviderRegistration registration, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return false;
        }

        string providerId = registration.Provider.Id;
        var timeout = new CancellationTokenSource(Options.ProviderTimeout, _timeProvider);
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        Task<ProviderUsage> work = ReadAsync(registration.Provider, linked.Token);
        bool handedOff = false;

        try
        {
            ProviderUsage usage;
            try
            {
                usage = await work.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown. A cancelled refresh raises no event, so closing the app never
                // looks like a provider failing, and whatever the abandoned call ends up
                // throwing is nobody's news.
                Observe(work);
                return false;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                var expired = new TimeoutException(
                    $"{providerId} did not answer within {Options.ProviderTimeout}.");
                ReportFailure(providerId, expired);
                handedOff = true;
                HandOff(registration, work, linked, timeout);
                RaiseUsageUpdated(Failed(providerId));
                return true;
            }
            catch (Exception ex)
            {
                // Including a provider that cancelled for its own reasons: from out here
                // that is a read which did not produce a number, which is an error
                // reading like any other.
                ReportFailure(providerId, ex);
                usage = Failed(providerId);
            }

            RaiseUsageUpdated(UsageReadingNormaliser.Normalise(usage, _timeProvider.GetUtcNow()));
            return false;
        }
        finally
        {
            if (!handedOff)
            {
                linked.Dispose();
                timeout.Dispose();
            }
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private static async Task<ProviderUsage> ReadAsync(IUsageProvider provider, CancellationToken ct)
    {
        await provider.RefreshAsync(ct).ConfigureAwait(false);
        return await provider.GetUsageAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Leaves an abandoned provider call holding the gate until it unwinds, and cleans up
    /// after it whenever that is.
    /// </summary>
    private static void HandOff(
        ProviderRegistration registration,
        Task<ProviderUsage> work,
        CancellationTokenSource linked,
        CancellationTokenSource timeout) =>
        _ = work.ContinueWith(
            static (finished, state) =>
            {
                // Observed deliberately: the abandoned call's outcome was reported as a
                // timeout already, and an unobserved fault helps nobody.
                _ = finished.Exception;

                (ProviderRegistration registration, CancellationTokenSource linked, CancellationTokenSource timeout) =
                    ((ProviderRegistration, CancellationTokenSource, CancellationTokenSource))state!;
                registration.Exit();
                linked.Dispose();
                timeout.Dispose();
            },
            (registration, linked, timeout),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

    /// <summary>
    /// The reading a failed refresh produces. There is no timestamp on it: nothing was
    /// read, so there is nothing for a "last refreshed" line to be about. The detail is
    /// the contract copy, never the exception text, which reaches a log through
    /// <see cref="ProviderFailed"/> instead.
    /// </summary>
    private static ProviderUsage Failed(string providerId) =>
        new(providerId, ProviderStatus.Error, [], null, null, ProviderUsage.UnavailableDetail);

    private void ReportFailure(string providerId, Exception exception)
    {
        EventHandler<ProviderFailure>? handlers = ProviderFailed;
        if (handlers is null)
        {
            return;
        }

        var failure = new ProviderFailure(providerId, exception, _timeProvider.GetUtcNow());
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ProviderFailure>)handler)(this, failure);
            }
            catch (Exception)
            {
                // A logger that throws is not allowed to stop monitoring either.
            }
        }
    }

    /// <summary>
    /// Announces a reading to each subscriber separately, so that one that throws cannot
    /// starve the subscribers behind it in the invocation list.
    /// </summary>
    private void RaiseUsageUpdated(ProviderUsage usage)
    {
        EventHandler<ProviderUsage>? handlers = UsageUpdated;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ProviderUsage>)handler)(this, usage);
            }
            catch (Exception)
            {
                // Contained: a view model that throws is its own problem.
            }
        }
    }

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
        if (registrations.Length == 0)
        {
            return;
        }

        // Concurrently: shutdown waits once for everything to unwind, not once per
        // provider, and gives up on anything that ignores cancellation.
        var waits = new Task<bool>[registrations.Length];
        for (int i = 0; i < registrations.Length; i++)
        {
            waits[i] = registrations[i].WaitForIdleAsync(Options.ShutdownTimeout);
        }

        bool[] entered = await Task.WhenAll(waits).ConfigureAwait(false);
        for (int i = 0; i < entered.Length; i++)
        {
            if (entered[i])
            {
                registrations[i].Exit();
            }
        }
    }

    private sealed class ProviderRegistration(IUsageProvider provider)
    {
        private int _dirty;
        private int _refreshing;

        public IUsageProvider Provider { get; } = provider;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>
        /// The handler subscribed to <see cref="IUsageProvider.UsageChanged"/>, kept so it
        /// can be unsubscribed on disposal.
        /// </summary>
        public EventHandler<ProviderUsage>? Push { get; set; }

        /// <summary>True while the scheduler is inside a refresh of this provider.</summary>
        public bool IsRefreshing => Volatile.Read(ref _refreshing) != 0;

        /// <summary>
        /// True when something asked for a refresh while one was in flight. The refresh in
        /// flight runs once more rather than leaving that change unseen.
        /// </summary>
        public bool IsDirty => Volatile.Read(ref _dirty) != 0;

        public void MarkDirty() => Volatile.Write(ref _dirty, 1);

        public void ClearDirty() => Volatile.Write(ref _dirty, 0);

        public void BeginRefresh() => Volatile.Write(ref _refreshing, 1);

        public void EndRefresh() => Volatile.Write(ref _refreshing, 0);

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
                _ = Gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed during shutdown; the count no longer matters.
            }
            catch (SemaphoreFullException)
            {
                // Released twice by a shutdown racing an abandoned call. Harmless.
            }
        }

        public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
        {
            try
            {
                return await Gate.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
}
