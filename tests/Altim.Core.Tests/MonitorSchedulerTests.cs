using Altim.Core.Models;
using Altim.Core.Monitoring;
using Altim.Core.Tests.Fakes;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The scheduler under a simulated clock: cadence, debounce, suspend and resume, the
/// network floor, non-overlap, and the containment of one provider failing.
/// </summary>
public sealed class MonitorSchedulerTests
{
    [Fact]
    public async Task TheRelaxedCadenceIsSixtySeconds()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);

        Assert.Equal(TimeSpan.FromSeconds(60), scheduler.CurrentInterval);
        scheduler.Start();

        // Fifty-nine seconds is not a tick and the sixtieth second is. That is read as
        // one refresh once the sixtieth second's refresh has finished, rather than as
        // "nothing yet" after a pause: the count only ever goes up, so finding one here is
        // proof that the fifty-nine seconds before it produced none.
        time.Advance(TimeSpan.FromSeconds(59));
        time.Advance(TimeSpan.FromSeconds(1));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);

        // And the one after it is a further sixty seconds away, not sooner.
        provider.ResetSignals();
        time.Advance(TimeSpan.FromSeconds(59));
        time.Advance(TimeSpan.FromSeconds(1));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(2, provider.RefreshCount);
    }

    [Fact]
    public async Task AnOpenWindowTightensTheCadenceToTenSeconds()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.Start();

        scheduler.SetUiVisible(true);
        Assert.Equal(TimeSpan.FromSeconds(10), scheduler.CurrentInterval);
        Assert.True(scheduler.IsUiVisible);

        time.Advance(TimeSpan.FromSeconds(10));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);

        provider.ResetSignals();
        scheduler.SetUiVisible(false);
        Assert.Equal(TimeSpan.FromSeconds(60), scheduler.CurrentInterval);

        // Ten seconds no longer buys a refresh and sixty does. One further refresh across
        // the whole sixty seconds is what says the tightened cadence was really dropped;
        // a scheduler still running at ten would have counted six by now.
        time.Advance(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(50));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(2, provider.RefreshCount);
    }

    [Fact]
    public async Task ABurstOfHintsProducesOneRefresh()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.Start();

        scheduler.Hint("claude");
        scheduler.Hint("claude");
        time.Advance(TimeSpan.FromMilliseconds(700));
        scheduler.Hint("claude");
        scheduler.Hint(null);

        // The window opened on the first hint and closes 750ms after it however many
        // hints landed inside it. Four hints, one refresh.
        time.Advance(TimeSpan.FromMilliseconds(50));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);

        // And nothing further until the next hint or the polling floor, which is the tick
        // at sixty seconds. Two refreshes by the time that one has finished, not three.
        provider.ResetSignals();
        time.Advance(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(29) + TimeSpan.FromMilliseconds(250));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(2, provider.RefreshCount);
    }

    [Fact]
    public async Task AHintAfterTheWindowClosesOpensANewOne()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.Start();

        scheduler.Hint("claude");
        time.Advance(TimeSpan.FromMilliseconds(750));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);

        provider.ResetSignals();
        scheduler.Hint("claude");
        time.Advance(TimeSpan.FromMilliseconds(750));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(2, provider.RefreshCount);
    }

    [Fact]
    public async Task AHintNamesOnlyTheProviderItIsAbout()
    {
        var time = new TestTimeProvider();
        var claude = new FakeUsageProvider("claude", time);
        var codex = new FakeUsageProvider("codex", time);
        await using var scheduler = new MonitorScheduler([claude, codex], time);
        scheduler.Start();

        scheduler.Hint("codex");
        time.Advance(TimeSpan.FromMilliseconds(750));
        await codex.RefreshCompleted.Task.WithCeiling();

        Assert.Equal(1, codex.RefreshCount);
        Assert.Equal(0, claude.RefreshCount);
    }

    [Fact]
    public async Task SuspendStopsEveryScheduledRefresh()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.Start();
        scheduler.Suspend();

        Assert.True(scheduler.IsSuspended);
        time.Advance(TimeSpan.FromMinutes(5));
        scheduler.Hint("claude");
        time.Advance(TimeSpan.FromSeconds(1));

        // Five minutes of ticks and a hint, all of them swallowed. Read behind StopAsync
        // rather than after a pause: it cancels the run, waits for the loop to unwind and
        // then for every provider gate, so anything the suspend had let through has
        // finished and counted by the time it returns.
        await scheduler.StopAsync().AsTask().WithCeiling();
        Assert.Equal(0, provider.RefreshCount);
    }

    [Fact]
    public async Task ResumeRefreshesExactlyOnce()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);

        // Started, but no tick is due, so the only thing that can refresh here is the
        // resume.
        scheduler.Start();
        scheduler.Suspend();
        scheduler.Resume();
        await provider.RefreshCompleted.Task.WithCeiling();

        Assert.False(scheduler.IsSuspended);
        Assert.Equal(1, provider.RefreshCount);

        // A platform that reports the resume twice must not refresh twice. Read at the
        // next poll tick: a second resume refresh would have counted well before that.
        provider.ResetSignals();
        scheduler.Resume();
        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(2, provider.RefreshCount);
    }

    [Fact]
    public async Task RefreshesForOneProviderNeverOverlap()
    {
        var time = new TestTimeProvider();
        var wedge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider("claude", time) { RefreshGate = wedge.Task };

        // The wedge is what holds this refresh open, and it has to be the only thing that
        // does. On the default thirty-second budget the three minutes advanced below take
        // the scheduler past ProviderTimeout: it abandons the call, hands the gate to it,
        // and gets the gate back the moment the abandoned call unwinds — after which a
        // later tick entering the provider is correct behaviour, not an overlap, and
        // whether it happens comes down to which thread pool item ran first. Abandonment
        // has its own test below; this one is about the gate.
        await using var scheduler = new MonitorScheduler(
            [provider],
            time,
            MonitorSchedulerOptions.Default with { ProviderTimeout = TimeSpan.FromMinutes(10) });
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshEntered.Task.WithCeiling();

        // Three more ticks while the first refresh is still in flight.
        time.Advance(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(60));

        // A tick is queued, so no test can await one. These two take the same per-provider
        // path an hour of ticks would, and they return a task: both have run all the way
        // to their decision, and both were dropped, before the count is read.
        await scheduler.RefreshAllAsync(TestContext.Current.CancellationToken);
        await scheduler.RefreshAsync("claude", TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.RefreshCount);
        Assert.Equal(1, provider.PeakConcurrentRefreshes);

        wedge.SetResult();
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.PeakConcurrentRefreshes);
    }

    [Fact]
    public async Task AnAbandonedRefreshHoldsTheGateUntilTheProviderActuallyUnwinds()
    {
        var time = new TestTimeProvider();
        var wedge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider("codex", time)
        {
            RefreshGate = wedge.Task,
            HonoursCancellation = false,
        };
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += collector.Handle;
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshEntered.Task.WithCeiling();

        // The budget runs out. The scheduler stops waiting and says so, but the call is
        // still inside the provider, so the gate it was holding stays taken: reading a
        // provider that is already in a call is exactly the overlap all of this exists to
        // prevent, and a timeout is not permission to do it.
        time.Advance(MonitorSchedulerOptions.Default.ProviderTimeout);
        await collector.WaitForAsync(1).WithCeiling();
        Assert.Equal(ProviderStatus.Error, Assert.Single(collector.Received).Status);

        await scheduler.RefreshAllAsync(TestContext.Current.CancellationToken);
        await scheduler.RefreshAsync("codex", TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.RefreshCount);
        Assert.Equal(1, provider.PeakConcurrentRefreshes);

        // It finally unwinds. The gate comes back with it — through a continuation on the
        // abandoned call, so there is no event to wait on — and the provider is read
        // again. Never twice at once, which is the part that matters.
        wedge.SetResult();
        await provider.RefreshCompleted.Task.WithCeiling();
        await TestWaits.UntilAsync(
            () => scheduler.RefreshAsync("codex", TestContext.Current.CancellationToken).AsTask(),
            () => provider.RefreshCount > 1);

        Assert.Equal(1, provider.PeakConcurrentRefreshes);
    }

    [Fact]
    public async Task OneProviderFailingDoesNotAffectTheOthers()
    {
        var time = new TestTimeProvider();
        var claude = new FakeUsageProvider("claude", time)
        {
            Usage = new ProviderUsage(
                "claude",
                ProviderStatus.Active,
                [new UsageMetric("five_hour", "Session", 53d, null, MetricConfidence.Documented)],
                null,
                null,
                null),
        };
        var codex = new FakeUsageProvider("codex", time)
        {
            RefreshFailure = new InvalidOperationException("app-server did not respond"),
        };

        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([claude, codex], time);
        scheduler.UsageUpdated += collector.Handle;
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await collector.WaitForAsync(2).WithCeiling();

        ProviderUsage failed = Assert.Single(collector.Received, u => u.ProviderId == "codex");
        Assert.Equal(ProviderStatus.Error, failed.Status);
        Assert.Equal(ProviderUsage.UnavailableDetail, failed.StatusDetail);
        Assert.Null(failed.LastRefreshed);
        Assert.Empty(failed.Metrics);
        Assert.Null(failed.Tokens);

        ProviderUsage healthy = Assert.Single(collector.Received, u => u.ProviderId == "claude");
        Assert.Equal(ProviderStatus.Active, healthy.Status);
        Assert.Equal<double?>(53d, Assert.Single(healthy.Metrics).UsedPercent);
        Assert.Equal(1, claude.RefreshCount);
    }

    [Fact]
    public async Task AFailingProviderKeepsBeingRefreshed()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("codex", time)
        {
            RefreshFailure = new InvalidOperationException("app-server did not respond"),
        };
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += collector.Handle;
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await collector.WaitForAsync(1).WithCeiling();
        provider.ResetSignals();

        time.Advance(TimeSpan.FromSeconds(60));
        await collector.WaitForAsync(2).WithCeiling();

        Assert.Equal(2, provider.RefreshCount);
        Assert.All(collector.Received, u => Assert.Equal(ProviderStatus.Error, u.Status));
    }

    [Fact]
    public async Task TheNetworkFloorGatesTheNetworkCallAndNotTheWholeRefresh()
    {
        var time = new TestTimeProvider();
        await using var scheduler = new MonitorScheduler(time);
        var provider = new FakeUsageProvider("codex", time) { NetworkGate = scheduler.NetworkGate };
        scheduler.Register(provider);
        scheduler.SetUiVisible(true);
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(10));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);
        Assert.Equal(1, provider.NetworkCallCount);

        // Five more ticks at the tightened cadence, all inside the network floor. The
        // provider is read every time, because its local sources cost nothing; only the
        // call that reaches the network is held back.
        for (int i = 0; i < 5; i++)
        {
            provider.ResetSignals();
            time.Advance(TimeSpan.FromSeconds(10));
            await provider.RefreshCompleted.Task.WithCeiling();
        }

        Assert.Equal(6, provider.RefreshCount);
        Assert.Equal(1, provider.NetworkCallCount);
        Assert.Equal(TimeSpan.FromSeconds(10), scheduler.NetworkGate.TimeUntilAvailable("codex"));

        provider.ResetSignals();
        time.Advance(TimeSpan.FromSeconds(10));
        await provider.RefreshCompleted.Task.WithCeiling();

        Assert.Equal(7, provider.RefreshCount);
        Assert.Equal(2, provider.NetworkCallCount);

        // And a "Retry" button on a provider that has never called can say so.
        Assert.Equal(TimeSpan.Zero, scheduler.NetworkGate.TimeUntilAvailable("claude"));
    }

    [Fact]
    public async Task ALocalProviderIsNotHeldBackByTheNetworkFloor()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler(time);
        scheduler.Register(provider);
        scheduler.SetUiVisible(true);
        scheduler.Start();

        for (int i = 0; i < 3; i++)
        {
            provider.ResetSignals();
            time.Advance(TimeSpan.FromSeconds(10));
            await provider.RefreshCompleted.Task.WithCeiling();
        }

        Assert.Equal(3, provider.RefreshCount);
    }

    [Fact]
    public async Task AnExplicitRefreshReportsThroughTheSameEvent()
    {
        var time = new TestTimeProvider();
        var claude = new FakeUsageProvider("claude", time);
        var codex = new FakeUsageProvider("codex", time);
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([claude, codex], time);
        scheduler.UsageUpdated += collector.Handle;

        await scheduler.RefreshAsync("codex", TestContext.Current.CancellationToken);

        Assert.Equal(1, codex.RefreshCount);
        Assert.Equal(0, claude.RefreshCount);
        Assert.Equal("codex", Assert.Single(collector.Received).ProviderId);

        collector.Reset();
        await scheduler.RefreshAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, collector.Count);
    }

    [Fact]
    public async Task StoppingIsIdempotentAndLeavesTheSchedulerRestartable()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);

        scheduler.Start();
        scheduler.Start();
        Assert.True(scheduler.IsRunning);

        await scheduler.StopAsync();
        await scheduler.StopAsync();
        Assert.False(scheduler.IsRunning);

        // Five minutes of ticks go by with the scheduler stopped. Read at the first
        // refresh after it is started again: if any of those five had got through, this
        // would not be the first.
        time.Advance(TimeSpan.FromMinutes(5));

        scheduler.Start();
        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task AWakeWithTheTimerRunningRefreshesOnceAndKeepsTicking()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.Start();

        // Asleep for less than one period, so nothing but the wake itself can refresh
        // here. A tick that comes due while suspended is skipped when the loop reaches it,
        // but the timer signals it whether the loop is there to be told or not, and one
        // left unconsumed is picked up as soon as the resume clears the flag — a second
        // refresh that says nothing about the wake. What a suspend does to ticks is
        // SuspendStopsEveryScheduledRefresh's subject.
        scheduler.Suspend();
        time.Advance(TimeSpan.FromSeconds(30));

        scheduler.Resume();
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);

        // And the poll timer is still the poll timer afterwards: it was never stopped, so
        // the tick it was already counting down to lands on its original schedule.
        provider.ResetSignals();
        time.Advance(TimeSpan.FromSeconds(30));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(2, provider.RefreshCount);
    }

    [Fact]
    public async Task OneSlowProviderDoesNotStarveTheOthersCadence()
    {
        var time = new TestTimeProvider();
        var wedge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FakeUsageProvider("codex", time) { RefreshGate = wedge.Task };
        var fast = new FakeUsageProvider("claude", time);

        // Long enough that codex is slow rather than hung: this is about cadence, not
        // about the timeout.
        await using var scheduler = new MonitorScheduler(
            [slow, fast],
            time,
            MonitorSchedulerOptions.Default with { ProviderTimeout = TimeSpan.FromMinutes(10) });
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await slow.RefreshEntered.Task.WithCeiling();
        await fast.RefreshCompleted.Task.WithCeiling();

        // codex is still inside its refresh. claude keeps its own cadence regardless.
        for (int i = 2; i <= 4; i++)
        {
            fast.ResetSignals();
            time.Advance(TimeSpan.FromSeconds(60));
            await fast.RefreshCompleted.Task.WithCeiling();
            Assert.Equal(i, fast.RefreshCount);
        }

        Assert.Equal(1, slow.RefreshCount);
        wedge.SetResult();
    }

    /// <summary>
    /// A request that arrives while a refresh is in flight is served once that refresh
    /// leaves, rather than waiting for the next tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every request this makes is one it can wait on, which is what makes the count on the
    /// far side exact: one refresh for the tick, one for the record it served. The explicit
    /// path returns a task, so the record is provably in place before the wedge is let go.
    /// </para>
    /// <para>
    /// It used to make that request through a hint as well, and that is what flaked: seen
    /// once as three refreshes where two were expected, on a machine running several builds
    /// at once. A hint's refresh is queued with <c>Task.Run</c> and nothing can await it, so
    /// a loaded runner is free to run it after the refresh it was meant to join has already
    /// left, and it then takes a turn of its own. That is three requests, three refreshes,
    /// none overlapping and none lost, which is the scheduler working rather than a defect;
    /// the exact count is simply not something the design promises once a request that
    /// cannot be awaited is in play. Reproduced by deferring that queued work by 50ms, which
    /// fails 2 against 3 every time. The scheduled tick the note here used to blame is not a
    /// candidate at all: the clock is manual, this advances it by sixty seconds in total,
    /// and the second tick is due at a hundred and twenty.
    /// </para>
    /// <para>
    /// The count stays exact rather than becoming a floor. A dropped record leaves one
    /// refresh, so "at least two" would pass against the defect this test is for the moment
    /// anything else refreshed at all. Keeping it exact costs the hint, which is covered by
    /// the tests that can wait on it: a burst of hints producing one refresh, a hint after
    /// the window closes opening a new one, and a push from inside a refresh not announcing
    /// twice.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARefreshRequestedWhileOneIsInFlightRunsOnceMoreOnExit()
    {
        var time = new TestTimeProvider();
        var wedge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider("claude", time) { RefreshGate = wedge.Task };
        var collector = new UsageCollector();

        // As in RefreshesForOneProviderNeverOverlap: the wedge holds this refresh open,
        // not the provider budget quietly running out underneath it.
        await using var scheduler = new MonitorScheduler(
            [provider],
            time,
            MonitorSchedulerOptions.Default with { ProviderTimeout = TimeSpan.FromMinutes(10) });
        scheduler.UsageUpdated += collector.Handle;
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshEntered.Task.WithCeiling();

        // A request lands while the refresh is in flight. Dropping it hides the change
        // until the next relaxed tick, which is up to a minute of staleness for a file that
        // has already been written. This one is the explicit refresh: it goes down the same
        // per-provider path as a hint or a tick, and it returns a task, so by the time it
        // has been awaited the request is recorded rather than queued somewhere.
        provider.RefreshGate = null;
        await scheduler.RefreshAsync("claude", TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.RefreshCount);

        // Exactly one more on the way out, and it is the record that produced it: nothing
        // else here can refresh this provider.
        wedge.SetResult();
        await collector.WaitForAsync(2).WithCeiling();
        Assert.Equal(2, provider.RefreshCount);
    }

    [Fact]
    public async Task ARefreshRequestedAsOneIsLeavingIsNotLost()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += collector.Handle;
        scheduler.Start();

        const int Ticks = 3000;
        for (int tick = 1; tick <= Ticks; tick++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
            await collector.WaitForAsync(tick).WithCeiling();
        }

        Assert.Equal(Ticks, provider.RefreshCount);
        Assert.Equal(Ticks, collector.Count);
    }

    [Fact]
    public async Task AHungProviderBecomesAnErrorReadingRatherThanSilence()
    {
        var time = new TestTimeProvider();
        var wedge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider("codex", time)
        {
            RefreshGate = wedge.Task,
            HonoursCancellation = false,
        };
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += collector.Handle;
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshEntered.Task.WithCeiling();

        // Being inside the provider is downstream of the budget's timer being armed, so
        // the advance below is certain to find it. No pause is needed to make that true.
        time.Advance(MonitorSchedulerOptions.Default.ProviderTimeout);
        await collector.WaitForAsync(1).WithCeiling();

        ProviderUsage reading = Assert.Single(collector.Received);
        Assert.Equal(ProviderStatus.Error, reading.Status);
        Assert.Equal(ProviderUsage.UnavailableDetail, reading.StatusDetail);
        Assert.Null(reading.LastRefreshed);

        wedge.SetResult();
    }

    [Fact]
    public async Task StoppingIsBoundedEvenWhenAProviderIgnoresCancellation()
    {
        var time = new TestTimeProvider();
        var wedge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider("codex", time)
        {
            RefreshGate = wedge.Task,
            HonoursCancellation = false,
        };
        await using var scheduler = new MonitorScheduler(
            [provider],
            time,
            MonitorSchedulerOptions.Default with { ShutdownTimeout = TimeSpan.FromMilliseconds(200) });
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshEntered.Task.WithCeiling();

        await scheduler.StopAsync().AsTask().WithCeiling();
        Assert.False(scheduler.IsRunning);

        wedge.SetResult();
    }

    [Fact]
    public async Task TheExceptionBehindAFailedReadingIsReportedForLogging()
    {
        var time = new TestTimeProvider();
        var failure = new InvalidOperationException(@"C:\Users\someone\.codex\sessions is unreadable");
        var provider = new FakeUsageProvider("codex", time) { RefreshFailure = failure };
        List<ProviderFailure> failures = [];
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += collector.Handle;
        scheduler.ProviderFailed += (_, reported) => failures.Add(reported);

        await scheduler.RefreshAllAsync(TestContext.Current.CancellationToken);

        // The user is told one sentence; the path stays in the log.
        ProviderUsage reading = Assert.Single(collector.Received);
        Assert.Equal(ProviderUsage.UnavailableDetail, reading.StatusDetail);

        ProviderFailure logged = Assert.Single(failures);
        Assert.Equal("codex", logged.ProviderId);
        Assert.Same(failure, logged.Exception);
    }

    [Fact]
    public async Task AReadingIsNormalisedBeforeItIsAnnounced()
    {
        var start = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var time = new TestTimeProvider(start);
        var provider = new FakeUsageProvider("claude", time)
        {
            Usage = new ProviderUsage(
                "claude",
                ProviderStatus.Active,
                [
                    new UsageMetric(
                        "five_hour",
                        "Session",
                        92d,
                        new LimitWindow(TimeSpan.FromMinutes(300), start.AddMinutes(-5)),
                        MetricConfidence.Documented),
                    new UsageMetric(
                        "seven_day",
                        "Weekly",
                        100.7d,
                        new LimitWindow(TimeSpan.FromMinutes(10_080), start.AddDays(2)),
                        MetricConfidence.Documented),
                    new UsageMetric(
                        "spend_limit",
                        "Spend",
                        11d,
                        new LimitWindow(TimeSpan.FromMinutes(300), start.AddSeconds(-30)),
                        MetricConfidence.Documented),
                ],
                null,
                null,
                null),
        };
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += collector.Handle;

        await scheduler.RefreshAllAsync(TestContext.Current.CancellationToken);

        ProviderUsage reading = Assert.Single(collector.Received);

        // The window ended five minutes ago and the provider is still serving the last
        // snapshot it has. A frozen 92% under "resets in under a minute" is worse than
        // saying nothing.
        UsageMetric stale = reading.Metrics[0];
        Assert.Null(stale.UsedPercent);
        Assert.False(stale.IsUsedPercentReported);
        Assert.Equal(TimeSpan.FromMinutes(300), stale.Window?.Length);
        Assert.Null(stale.Window?.ResetsAt);

        // A live window keeps its reading, clamped.
        Assert.Equal<double?>(100d, reading.Metrics[1].UsedPercent);
        Assert.Equal<DateTimeOffset?>(start.AddDays(2), reading.Metrics[1].Window?.ResetsAt);

        // Thirty seconds past is inside the clock-skew grace and still counts as live.
        Assert.Equal<double?>(11d, reading.Metrics[2].UsedPercent);
    }

    [Fact]
    public async Task AProviderPushingAnUpdateIsTakenAsAHint()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.Start();

        // A watcher inside the provider noticed something. It is a hint like any other:
        // debounced, and never a reading in its own right.
        provider.RaiseUsageChanged();
        provider.RaiseUsageChanged();
        time.Advance(TimeSpan.FromMilliseconds(750));

        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task APushFromInsideARefreshDoesNotAnnounceTwice()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += collector.Handle;
        scheduler.Start();

        // FakeUsageProvider raises UsageChanged from inside every refresh, which is what a
        // real provider does when a reading moves. The scheduler is about to announce that
        // reading itself, so the event must not turn into a second refresh.
        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshCompleted.Task.WithCeiling();

        // The debounce window the push would have opened closes here. Read at the next
        // poll tick instead of after a pause: a refresh born of the push would have
        // counted long before that tick's reading arrived.
        time.Advance(TimeSpan.FromMilliseconds(750));
        time.Advance(TimeSpan.FromSeconds(59) + TimeSpan.FromMilliseconds(250));
        await collector.WaitForAsync(2).WithCeiling();

        Assert.Equal(2, provider.RefreshCount);
        Assert.Equal(2, collector.Count);
    }

    [Fact]
    public async Task AHintArrivingDuringShutdownIsANoOp()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        var scheduler = new MonitorScheduler([provider], time);
        scheduler.Start();
        await scheduler.DisposeAsync();

        // A filesystem event that was already in flight when the process started closing.
        // Disposal stopped the loop, waited for in-flight work and disposed the timers
        // before returning, and every call below is a synchronous no-op against that, so
        // there is nothing pending for a pause to let through and none is taken.
        scheduler.Hint("claude");
        scheduler.Hint();
        provider.RaiseUsageChanged();
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, provider.RefreshCount);

        // Registering or starting a disposed scheduler is still a programming error.
        Assert.Throws<ObjectDisposedException>(() => scheduler.Register(new FakeUsageProvider("codex", time)));
        Assert.Throws<ObjectDisposedException>(scheduler.Start);
    }

    [Fact]
    public async Task NothingRefreshesOnceTheSchedulerHasStopped()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.Start();

        scheduler.Hint("claude");
        await scheduler.StopAsync();

        // StopAsync disarmed the hint window and the poll timer and waited for in-flight
        // work before returning, so the open hint window dies with it.
        time.Advance(TimeSpan.FromMilliseconds(750));
        Assert.Equal(0, provider.RefreshCount);

        // A resume with no run to attach to would otherwise refresh with no way to cancel
        // it.
        scheduler.Suspend();
        scheduler.Resume();
        Assert.Equal(0, provider.RefreshCount);

        // Started again, the first tick is the first refresh: nothing the stopped
        // scheduler was asked to do got through behind these assertions either.
        scheduler.Start();
        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task AThrowingSubscriberDoesNotStarveTheOnesAfterIt()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("claude", time);
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += static (_, _) => throw new InvalidOperationException("bad subscriber");
        scheduler.UsageUpdated += collector.Handle;

        await scheduler.RefreshAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, collector.Count);
    }

    [Fact]
    public async Task AProviderCancellingItselfIsReportedAsAFailedReading()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("codex", time)
        {
            RefreshFailure = new OperationCanceledException("the app-server call gave up"),
        };
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += collector.Handle;

        await scheduler.RefreshAllAsync(TestContext.Current.CancellationToken);

        ProviderUsage reading = Assert.Single(collector.Received);
        Assert.Equal(ProviderStatus.Error, reading.Status);
        Assert.Equal(ProviderUsage.UnavailableDetail, reading.StatusDetail);
    }

    [Fact]
    public void TheRateLimiterLetsTheFirstCallThroughAndHoldsTheRestBack()
    {
        var time = new TestTimeProvider();
        var limiter = new RefreshRateLimiter(time, TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.Zero, limiter.TimeUntilAvailable("codex"));
        Assert.True(limiter.TryAcquire("codex"));
        Assert.False(limiter.TryAcquire("codex"));
        Assert.Equal(TimeSpan.FromMinutes(1), limiter.TimeUntilAvailable("codex"));

        // Keys are independent.
        Assert.True(limiter.TryAcquire("claude"));

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(limiter.TryAcquire("codex"));
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.TimeUntilAvailable("codex"));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire("codex"));
    }
}
