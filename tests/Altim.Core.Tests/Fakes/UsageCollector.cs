using Altim.Core.Models;

namespace Altim.Core.Tests.Fakes;

/// <summary>
/// Collects the readings a scheduler raises and lets a test wait until a given number have
/// arrived, which is the only deterministic way to observe work that finishes on a
/// background thread.
/// </summary>
public sealed class UsageCollector
{
    private readonly List<ProviderUsage> _received = [];
    private readonly object _gate = new();
    private TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _expected = int.MaxValue;

    public IReadOnlyList<ProviderUsage> Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _received.Count;
            }
        }
    }

    public void Handle(object? sender, ProviderUsage usage)
    {
        lock (_gate)
        {
            _received.Add(usage);
            if (_received.Count >= _expected)
            {
                _ = _reached.TrySetResult();
            }
        }
    }

    /// <summary>Completes once <paramref name="count"/> readings have been collected.</summary>
    public Task WaitForAsync(int count)
    {
        lock (_gate)
        {
            _expected = count;
            if (_received.Count >= count)
            {
                return Task.CompletedTask;
            }

            if (_reached.Task.IsCompleted)
            {
                _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return _reached.Task;
        }
    }

    /// <summary>Forgets everything collected so far and arms the wait again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _received.Clear();
            _expected = int.MaxValue;
            _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
