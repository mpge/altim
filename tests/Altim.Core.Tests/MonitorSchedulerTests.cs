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

        // No poll timer running, so the only thing that can refresh here is the resume.
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
        Assert.Equal("app-server did not respond", failed.StatusDetail);
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
    public async Task ANetworkTouchingProviderNeverRefreshesFasterThanOnceAMinute()
    {
        var time = new TestTimeProvider();
        var provider = new FakeUsageProvider("codex", time);
        await using var scheduler = new MonitorScheduler(time);
        scheduler.Register(provider, touchesNetwork: true);
        scheduler.SetUiVisible(true);
        scheduler.Start();

        time.Advance(TimeSpan.FromSeconds(10));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(1, provider.RefreshCount);
        provider.ResetSignals();

        // Five more ticks at the tightened cadence: all inside the network floor.
        for (int i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await TestWaits.SettleAsync();
        }

        Assert.Equal(1, provider.RefreshCount);

        time.Advance(TimeSpan.FromSeconds(10));
        await provider.RefreshCompleted.Task.WithCeiling();
        Assert.Equal(2, provider.RefreshCount);
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
