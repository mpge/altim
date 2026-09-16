namespace Altim.Core.Tests.Fakes;

/// <summary>
/// Bounded waits. A test that hangs tells nobody anything, so every wait on background
/// work carries a ceiling and fails as a timeout instead.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no "give the background a moment" helper here. Sleeping for a
/// fixed span and then asserting that something did <em>not</em> happen measures the
/// machine rather than the scheduler. On a loaded runner the queued work has not started
/// yet, so the assertion passes for the wrong reason; and anything the fake clock
/// legitimately set in motion during that sleep — a provider budget expiring, a debounce
/// window closing — turns the same assertion red on the same runner for no bug at all.
/// </para>
/// <para>
/// A negative claim is made in these tests one of three ways instead. Anchor it to a
/// positive, awaited event that happens after the thing being ruled out would have
/// happened, and read a cumulative counter there. Or drive the same code path through a
/// call that returns a task, so the drop can be awaited rather than guessed at. Or, where
/// the scenario is synchronous end to end — a disposed scheduler, a stopped one — assert
/// it immediately, because there is nothing pending for a pause to let through.
/// </para>
/// </remarks>
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
    /// Repeats <paramref name="attempt"/> until <paramref name="reached"/> holds, or fails
    /// as a timeout.
    /// </summary>
    /// <param name="attempt">The call to retry. Must be safe to run more than once.</param>
    /// <param name="reached">The state being waited for.</param>
    /// <remarks>
    /// For the one thing the scheduler releases through a background continuation rather
    /// than announcing: a gate handed to an abandoned provider call comes back whenever
    /// that call unwinds, and no event marks the moment. The wait still ends on the state
    /// it is waiting for rather than on a clock, so it cannot pass early on a fast machine
    /// nor fail on a loaded one.
    /// </remarks>
    public static async Task UntilAsync(Func<Task> attempt, Func<bool> reached)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(reached);

        long deadline = Environment.TickCount64 + (long)Ceiling.TotalMilliseconds;
        while (!reached())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException($"The expected state was not reached within {Ceiling}.");
            }

            await attempt().ConfigureAwait(false);
        }
    }
}
