using Altim.Core.Abstractions;
using Altim.Core.Models;

namespace Altim.UI.Tests.Fakes;

/// <summary>
/// A provider the tests drive.
/// </summary>
/// <remarks>
/// <see cref="Block"/> is the important one: it blocks the calling thread before the first
/// await, which is what a provider opening a database or running a CLI actually does. A view
/// model that read it on the dispatcher thread would freeze the window, and the block is how
/// the test proves it does not.
/// </remarks>
internal sealed class FakeUsageProvider : IUsageProvider
{
    private ProviderUsage _usage;

    /// <summary>Initializes a provider.</summary>
    /// <param name="id">Its identifier.</param>
    /// <param name="displayName">Its name, as shown.</param>
    /// <param name="usage">The reading it returns.</param>
    public FakeUsageProvider(string id, string displayName, ProviderUsage? usage = null)
    {
        Id = id;
        DisplayName = displayName;
        _usage = usage ?? Readings.Healthy(id);
        Status = _usage.Status;
    }

    /// <inheritdoc />
    public event EventHandler<ProviderUsage>? UsageChanged;

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public ProviderStatus Status { get; set; }

    /// <summary>The reading the provider currently holds.</summary>
    public ProviderUsage Reading => _usage;

    /// <summary>The sessions the provider reports.</summary>
    public IReadOnlyList<AgentSession> Sessions { get; set; } = [];

    /// <summary>Thrown from the next reading, when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Blocks the calling thread inside a reading until it is set.</summary>
    public ManualResetEventSlim? Block { get; set; }

    /// <summary>How many readings have been taken.</summary>
    public int UsageReads { get; private set; }

    /// <summary>How many refreshes have been asked for.</summary>
    public int Refreshes { get; private set; }

    /// <summary>Replaces the reading without announcing it.</summary>
    /// <param name="usage">The new reading.</param>
    public void Set(ProviderUsage usage)
    {
        _usage = usage;
        Status = usage.Status;
    }

    /// <summary>Replaces the reading and announces it, as a provider does.</summary>
    /// <param name="usage">The new reading.</param>
    public void Push(ProviderUsage usage)
    {
        Set(usage);
        UsageChanged?.Invoke(this, usage);
    }

    /// <inheritdoc />
    public ValueTask<ProviderUsage> GetUsageAsync(CancellationToken ct)
    {
        UsageReads++;
        Block?.Wait(ct);

        return Failure is { } failure
            ? ValueTask.FromException<ProviderUsage>(failure)
            : ValueTask.FromResult(_usage);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken ct) =>
        ValueTask.FromResult(Sessions);

    /// <inheritdoc />
    public ValueTask RefreshAsync(CancellationToken ct)
    {
        Refreshes++;
        return ValueTask.CompletedTask;
    }
}
