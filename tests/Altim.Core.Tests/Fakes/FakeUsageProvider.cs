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
            _ = RefreshEntered.TrySetResult();

            if (RefreshGate is { } gate)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
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
