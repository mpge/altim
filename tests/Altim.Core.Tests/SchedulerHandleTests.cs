using Altim.Core.Monitoring;
using Altim.Core.Tests.Fakes;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The one level of indirection between whatever produces a hint and whichever scheduler is
/// current.
/// </summary>
/// <remarks>
/// <para>
/// A scheduler's cadences are fixed at construction, so changing the refresh interval in
/// Settings replaces the instance. Everything that was holding the old one is then holding a
/// disposed object, and a disposed scheduler does not throw on a hint — it is documented not
/// to, because a filesystem watcher is still delivering events while the process closes its
/// windows. The two properties combine into a silent failure: the filesystem watchers go on
/// hinting a scheduler that has stopped, the meters stop moving on anything but the polling
/// floor, and "event-driven first" quietly becomes false until the next restart.
/// </para>
/// <para>
/// The same rebuild can also land in the middle of the first-readings pass, which is the one
/// pass that has to happen: it is the walk that gives the incremental cursors somewhere to
/// start from. A pass that was already inside the old scheduler when it was replaced reads
/// nothing and reports nothing, so the handle follows the rebuild and takes the reading
/// again.
/// </para>
/// </remarks>
public sealed class SchedulerHandleTests
{
    [Fact]
    public async Task AHintReachesTheSchedulerThatIsCurrentRatherThanTheOneBoundFirst()
    {
        var time = new TestTimeProvider();
        var first = new FakeUsageProvider("claude", time);
        var second = new FakeUsageProvider("claude", time);

        var handle = new SchedulerHandle();

        await using (var original = new MonitorScheduler([first], time))
        {
            original.Start();
            handle.Bind(original);

            await using var replacement = new MonitorScheduler([second], time);
            replacement.Start();
            handle.Bind(replacement);

            // What ProviderHintWatcher does on a filesystem event, through the handle it was
            // built with rather than through a scheduler instance it captured.
            handle.Hint("claude");
            time.Advance(TimeSpan.FromMilliseconds(750));

            await second.RefreshCompleted.Task.WithCeiling();
            Assert.Equal(1, second.RefreshCount);

            // The scheduler that was replaced is not refreshed by a hint delivered after the
            // rebuild. Read after the replacement's refresh has finished, so this is a
            // cumulative count at a known point rather than a guess after a pause.
            Assert.Equal(0, first.RefreshCount);
        }
    }

    [Fact]
    public void ANullBindingDropsHintsRatherThanThrowing()
    {
        var handle = new SchedulerHandle();

        Assert.Null(handle.Current);

        // A hint that arrives before the scheduler exists, or after it has gone, is an
        // ordinary outcome: the polling floor covers it.
        handle.Hint("claude");
        handle.Hint();
    }

    [Fact]
    public async Task ARebuildThatLandsDuringTheFirstReadingsPassTakesTheReadingAgain()
    {
        var time = new TestTimeProvider();
        var first = new FakeUsageProvider("claude", time);
        var second = new FakeUsageProvider("claude", time);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.RefreshGate = held.Task;

        var handle = new SchedulerHandle();

        await using var original = new MonitorScheduler([first], time);
        await using var replacement = new MonitorScheduler([second], time);

        original.Start();
        replacement.Start();
        handle.Bind(original);

        Task pass = handle.RefreshAllAsync(TestContext.Current.CancellationToken);

        // Inside the first provider's read, which is where a settings change lands when the
        // user opens Settings while the expensive first walk is still running.
        await first.RefreshEntered.Task.WithCeiling();
        handle.Bind(replacement);
        held.SetResult();

        await pass.WithCeiling();

        Assert.Equal(1, first.RefreshCount);
        Assert.Equal(1, second.RefreshCount);
        Assert.Same(replacement, handle.Current);
    }

    [Fact]
    public async Task APassWithNothingBoundIsANoOp()
    {
        var handle = new SchedulerHandle();
        await handle.RefreshAllAsync(TestContext.Current.CancellationToken).WithCeiling();
    }

    [Fact]
    public async Task EveryBindIsANewGeneration()
    {
        var time = new TestTimeProvider();
        var handle = new SchedulerHandle();

        int initial = handle.Generation;

        await using var scheduler = new MonitorScheduler([new FakeUsageProvider("claude", time)], time);
        handle.Bind(scheduler);

        Assert.NotEqual(initial, handle.Generation);
        Assert.Same(scheduler, handle.Current);

        int bound = handle.Generation;
        handle.Bind(null);

        Assert.NotEqual(bound, handle.Generation);
        Assert.Null(handle.Current);
    }
}
