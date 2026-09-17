using Altim.Core.Abstractions;
using Altim.Core.Models;

namespace Altim.Core.Tests.Fakes;

/// <summary>
/// A provider whose every behaviour is set by the test: how long a refresh takes, whether
/// it throws, and what it reports afterwards.
/// </summary>
public sealed class FakeUsageProvider : IUsageProvider
{
    private readonly TimeProvider _timeProvider;
    private int _refreshCount;
    private int _networkCallCount;
    private int _concurrentRefreshes;
    private int _peakConcurrentRefreshes;

    public FakeUsageProvider(string id, TimeProvider timeProvider)
    {
        Id = id;
        _timeProvider = timeProvider;
        Usage = new ProviderUsage(id, ProviderStatus.Idle, [], null, null, null);
    }

    public event EventHandler<ProviderUsage>? UsageChanged;

    public string Id { get; }

    public string DisplayName => Id;

    public ProviderStatus Status { get; private set; } = ProviderStatus.Unknown;

    /// <summary>The reading <see cref="GetUsageAsync"/> returns.</summary>
    public ProviderUsage Usage { get; set; }

    /// <summary>Thrown from <see cref="RefreshAsync"/> when set.</summary>
    public Exception? RefreshFailure { get; set; }

    /// <summary>Awaited inside <see cref="RefreshAsync"/> when set, to hold a refresh open.</summary>
    public Task? RefreshGate { get; set; }

    /// <summary>
    /// False to model a provider that never looks at the token it was handed, which is
    /// the provider shutdown has to survive.
    /// </summary>
    public bool HonoursCancellation { get; set; } = true;

    /// <summary>
    /// The gate this provider puts its own network call behind, or <see langword="null"/>
    /// when it has no network call to make.
    /// </summary>
    public IRefreshGate? NetworkGate { get; set; }

    /// <summary>How many times the gated network call actually ran.</summary>
    public int NetworkCallCount => Volatile.Read(ref _networkCallCount);

    /// <summary>Signalled at the moment <see cref="RefreshAsync"/> is entered.</summary>
    public TaskCompletionSource RefreshEntered { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signalled at the moment <see cref="RefreshAsync"/> returns.</summary>
    public TaskCompletionSource RefreshCompleted { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int RefreshCount => Volatile.Read(ref _refreshCount);

    /// <summary>The most refreshes that were ever in flight at once.</summary>
    public int PeakConcurrentRefreshes => Volatile.Read(ref _peakConcurrentRefreshes);

    /// <summary>Replaces the one-shot signals so the next refresh can be awaited again.</summary>
    public void ResetSignals()
    {
        RefreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RefreshCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async ValueTask RefreshAsync(CancellationToken ct)
    {
        int running = Interlocked.Increment(ref _concurrentRefreshes);
        int peak = Volatile.Read(ref _peakConcurrentRefreshes);
        while (running > peak && Interlocked.CompareExchange(ref _peakConcurrentRefreshes, running, peak) != peak)
        {
            peak = Volatile.Read(ref _peakConcurrentRefreshes);
        }

        try
        {
            _ = Interlocked.Increment(ref _refreshCount);

            // Read before entry is announced, not after. A test that awaits RefreshEntered
            // and then clears the gate would otherwise race this turn for it: the waiter
            // resumes on another thread the moment entry is signalled, and if it wins, this
            // turn sees no gate, runs straight through, and a test written to hold one
            // refresh open silently holds none. That failed on Linux and passed on Windows,
            // which is the signature of scheduling deciding the outcome.
            Task? gate = RefreshGate;

            _ = RefreshEntered.TrySetResult();

            if (gate is not null)
            {
                if (HonoursCancellation)
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await gate.ConfigureAwait(false);
                }
            }

            // Only the network call is gated, never the whole read: the local sources are
            // free either way.
            if (NetworkGate is null || NetworkGate.TryAcquire(Id))
            {
                _ = Interlocked.Increment(ref _networkCallCount);
            }

            if (RefreshFailure is { } failure)
            {
                Status = ProviderStatus.Error;
                throw failure;
            }

            Status = Usage.Status;
            UsageChanged?.Invoke(this, Usage);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _concurrentRefreshes);
            _ = RefreshCompleted.TrySetResult();
        }
    }

    /// <summary>Raises <see cref="UsageChanged"/> the way a provider watcher would.</summary>
    public void RaiseUsageChanged() => UsageChanged?.Invoke(this, Usage);

    public ValueTask<ProviderUsage> GetUsageAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Usage with { LastRefreshed = _timeProvider.GetUtcNow() });
    }

    public ValueTask<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentSession>>([]);
    }
}
