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

        time.Advance(TimeSpan.FromSeconds(59));
        await TestWaits.SettleAsync();
        Assert.Equal(0, provider.RefreshCount);

        time.Advance(TimeSpan.FromSeconds(1));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);
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

        time.Advance(TimeSpan.FromSeconds(10));
        await TestWaits.SettleAsync();
        Assert.Equal(1, provider.RefreshCount);

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
        await TestWaits.SettleAsync();
        Assert.Equal(0, provider.RefreshCount);

        time.Advance(TimeSpan.FromMilliseconds(50));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);

        // And nothing further until the next hint or the polling floor.
        time.Advance(TimeSpan.FromSeconds(30));
        await TestWaits.SettleAsync();
        Assert.Equal(1, provider.RefreshCount);
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
        await TestWaits.SettleAsync();

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
        await TestWaits.SettleAsync();

        Assert.False(scheduler.IsSuspended);
        Assert.Equal(1, provider.RefreshCount);

        // A platform that reports the resume twice must not refresh twice.
        scheduler.Resume();
        await TestWaits.SettleAsync();
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public async Task RefreshesForOneProviderNeverOverlap()
    {
        var time = new TestTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider("claude", time) { RefreshGate = gate.Task };
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshEntered.Task.WithCeiling();

        // Three more ticks while the first refresh is still in flight.
        time.Advance(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(60));
        await TestWaits.SettleAsync();

        Assert.Equal(1, provider.RefreshCount);
        Assert.Equal(1, provider.PeakConcurrentRefreshes);

        gate.SetResult();
        await provider.RefreshCompleted.Task.WithCeiling();
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

        time.Advance(TimeSpan.FromMinutes(5));
        await TestWaits.SettleAsync();
        Assert.Equal(0, provider.RefreshCount);

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

        scheduler.Suspend();

        // Five ticks go by with the machine asleep. They coalesce into nothing, because a
        // laptop coming out of sleep must not produce a backlog.
        time.Advance(TimeSpan.FromMinutes(5));
        await TestWaits.SettleAsync();
        Assert.Equal(0, provider.RefreshCount);

        scheduler.Resume();
        await provider.RefreshCompleted.Task.WithCeiling();
        await TestWaits.SettleAsync();
        Assert.Equal(1, provider.RefreshCount);

        // And the poll timer is still the poll timer afterwards.
        provider.ResetSignals();
        time.Advance(TimeSpan.FromSeconds(60));
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

    [Fact]
    public async Task ARefreshRequestedWhileOneIsInFlightRunsOnceMoreOnExit()
    {
        var time = new TestTimeProvider();
        var wedge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider("claude", time) { RefreshGate = wedge.Task };
        var collector = new UsageCollector();
        await using var scheduler = new MonitorScheduler([provider], time);
        scheduler.UsageUpdated += collector.Handle;
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(60));
        await provider.RefreshEntered.Task.WithCeiling();

        // A hint lands while the refresh is in flight. Dropping it hides the change until
        // the next relaxed tick, which is up to a minute of staleness for a file that has
        // already been written.
        provider.RefreshGate = null;
        scheduler.Hint("claude");
        time.Advance(TimeSpan.FromMilliseconds(750));
        await TestWaits.SettleAsync();
        Assert.Equal(1, provider.RefreshCount);

        wedge.SetResult();
        await collector.WaitForAsync(2).WithCeiling();
        Assert.Equal(2, provider.RefreshCount);
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
        await TestWaits.SettleAsync();

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
        time.Advance(TimeSpan.FromMilliseconds(750));
        await TestWaits.SettleAsync();

        Assert.Equal(1, provider.RefreshCount);
        Assert.Equal(1, collector.Count);
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
        scheduler.Hint("claude");
        scheduler.Hint();
        provider.RaiseUsageChanged();
        time.Advance(TimeSpan.FromMinutes(5));
        await TestWaits.SettleAsync();

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

        time.Advance(TimeSpan.FromMilliseconds(750));
        await TestWaits.SettleAsync();
        Assert.Equal(0, provider.RefreshCount);

        // A resume with no run to attach to would otherwise refresh with no way to cancel
        // it.
        scheduler.Suspend();
        scheduler.Resume();
        await TestWaits.SettleAsync();
        Assert.Equal(0, provider.RefreshCount);
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
