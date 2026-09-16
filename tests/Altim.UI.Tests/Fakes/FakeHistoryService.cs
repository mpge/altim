using Altim.Core.Abstractions;
using Altim.Core.Models;

namespace Altim.UI.Tests.Fakes;

/// <summary>
/// A history store the tests fill by hand.
/// </summary>
/// <remarks>
/// <see cref="GetLatestBeforeAsync"/> answers the way the contract describes: the most recent
/// sample per metric key strictly before the instant, and nothing when there is none. The
/// history page depends on that shape to draw the left edge of a range nothing changed in.
/// </remarks>
internal sealed class FakeHistoryService : IUsageHistoryService
{
    private readonly List<UsageSample> _samples = [];
    private TaskCompletionSource _queried = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How many times the store has been emptied.</summary>
    public int Clears { get; private set; }

    /// <summary>How many range queries have been asked for.</summary>
    public int RangeReads { get; private set; }

    /// <summary>How many carry-in queries have been asked for.</summary>
    public int CarryInReads { get; private set; }

    /// <summary>The start of the last range asked for, or null when none has been.</summary>
    public DateTimeOffset? LastFrom { get; private set; }

    /// <summary>The end of the last range asked for, or null when none has been.</summary>
    public DateTimeOffset? LastTo { get; private set; }

    /// <summary>The instant the last carry-in was asked for, or null when none has been.</summary>
    public DateTimeOffset? LastCarryInAt { get; private set; }

    /// <summary>How far back the last range asked for reached, or null when none has been.</summary>
    public TimeSpan? LastSpan => LastTo - LastFrom;

    /// <summary>Whether the next read throws.</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// Completes when the next range query lands. The history page reloads itself when the span
    /// changes, without anything being awaited, so a test that polled for it would be a flaky one.
    /// </summary>
    public Task Queried => _queried.Task;

    /// <summary>Arms <see cref="Queried"/> for the query after this point.</summary>
    public void ExpectQuery() => _queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Adds samples to the store.</summary>
    /// <param name="samples">The samples to add.</param>
    public void Add(params UsageSample[] samples) => _samples.AddRange(samples);

    /// <inheritdoc />
    public ValueTask RecordAsync(ProviderUsage usage, CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<UsageSample>> GetRangeAsync(
        string providerId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        RangeReads++;
        LastFrom = from;
        LastTo = to;
        _queried.TrySetResult();

        if (Failure is { } failure)
        {
            return ValueTask.FromException<IReadOnlyList<UsageSample>>(failure);
        }

        List<UsageSample> kept = [];
        foreach (UsageSample sample in _samples)
        {
            if (string.Equals(sample.ProviderId, providerId, StringComparison.Ordinal)
                && sample.CapturedAt >= from
                && sample.CapturedAt <= to)
            {
                kept.Add(sample);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<UsageSample>>(kept);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<UsageSample>> GetLatestBeforeAsync(
        string providerId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        CarryInReads++;
        LastCarryInAt = at;

        if (Failure is { } failure)
        {
            return ValueTask.FromException<IReadOnlyList<UsageSample>>(failure);
        }

        Dictionary<string, UsageSample> latest = [];
        foreach (UsageSample sample in _samples)
        {
            if (!string.Equals(sample.ProviderId, providerId, StringComparison.Ordinal)
                || sample.CapturedAt >= at)
            {
                continue;
            }

            if (!latest.TryGetValue(sample.MetricKey, out UsageSample? seen)
                || sample.CapturedAt > seen.CapturedAt)
            {
                latest[sample.MetricKey] = sample;
            }
        }

        return ValueTask.FromResult<IReadOnlyList<UsageSample>>([.. latest.Values]);
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken ct)
    {
        Clears++;
        _samples.Clear();
        return ValueTask.CompletedTask;
    }
}
