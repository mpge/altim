namespace Altim.Core.Tests.Fakes;

/// <summary>
/// Bounded waits. A test that hangs tells nobody anything, so every wait on background
/// work carries a ceiling and fails as a timeout instead.
/// </summary>
public static class TestWaits
{
    /// <summary>The ceiling for any wait on background work in these tests.</summary>
    public static TimeSpan Ceiling { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Waits for a task, failing the test rather than hanging it.</summary>
    public static Task WithCeiling(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.WaitAsync(Ceiling, CancellationToken.None);
    }

    /// <summary>
    /// Gives background work a moment to happen when the assertion is that it does
    /// <em>not</em> happen. Real time, not fake: nothing is scheduled against the fake
    /// clock here, so this only yields the thread pool.
    /// </summary>
    public static Task SettleAsync() => Task.Delay(TimeSpan.FromMilliseconds(150), CancellationToken.None);
}
