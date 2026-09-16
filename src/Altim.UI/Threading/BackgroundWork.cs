namespace Altim.UI.Threading;

/// <summary>
/// Moves a provider or storage call off the dispatcher thread.
/// </summary>
/// <remarks>
/// <para>
/// The contracts in <c>Altim.Core</c> are asynchronous, but an implementation is free to open a
/// database, run a CLI or read a 28GB session directory before its first await. Handing the call
/// to the thread pool means a slow implementation costs a frame of nothing rather than a frozen
/// window, and the caller's continuation still returns to the dispatcher.
/// </para>
/// </remarks>
internal static class BackgroundWork
{
    /// <summary>Runs an asynchronous call that returns a value on the thread pool.</summary>
    /// <typeparam name="TResult">The call's result.</typeparam>
    /// <param name="work">The call to run.</param>
    /// <param name="ct">Cancels the call.</param>
    public static Task<TResult> RunAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(async () => await work(ct).ConfigureAwait(false), ct);
    }

    /// <summary>Runs an asynchronous call that returns nothing on the thread pool.</summary>
    /// <param name="work">The call to run.</param>
    /// <param name="ct">Cancels the call.</param>
    public static Task RunAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(async () => await work(ct).ConfigureAwait(false), ct);
    }
}
