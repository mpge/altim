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
    private readonly Dictionary<(string Provider, DateOnly Day), UsageDay> _days = [];
    private TaskCompletionSource _queried = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How many day reads have been asked for.</summary>
    public int DayReads { get; private set; }

    /// <summary>
    /// The managed thread the last day read ran on, or null when none has been. A year of
    /// days is read for every provider each time the page opens, so the test that proves it
    /// is not read on the dispatcher compares this against the thread it is asserting from.
    /// </summary>
    public int? DaysReadOnThreadId { get; private set; }

    /// <summary>
    /// Held by a test that wants a day read to stay in flight. The read waits on it before
    /// answering, which is how a test proves a view model did not await a load in its
    /// constructor.
    /// </summary>
    public TaskCompletionSource? DayGate { get; set; }

    /// <summary>
    /// Set the moment a day read begins, before it waits on <see cref="DayGate"/>. A test
    /// that wants to prove no read was started cannot do it by looking at a counter straight
    /// away - a read handed to the thread pool has not necessarily reached it yet - so it
    /// waits on this instead and asserts the wait times out.
    /// </summary>
    public ManualResetEventSlim? DayEntered { get; set; }

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
    /// Providers whose day reads throw while the rest answer normally, which is how one
    /// unreadable store is told apart from an empty one.
    /// </summary>
    public HashSet<string> UnreadableProviders { get; } = new(StringComparer.Ordinal);

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

    /// <summary>Adds days to the store, bypassing the precedence rule.</summary>
    /// <param name="days">The days to add.</param>
    public void AddDays(params UsageDay[] days)
    {
        ArgumentNullException.ThrowIfNull(days);
        foreach (UsageDay day in days)
        {
            _days[(day.ProviderId, day.Day)] = day;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<UsageDay>> GetDaysAsync(
        string providerId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        DayReads++;
        DaysReadOnThreadId = Environment.CurrentManagedThreadId;
        DayEntered?.Set();

        if (DayGate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        if (UnreadableProviders.Contains(providerId))
        {
            throw new IOException("locked");
        }

        List<UsageDay> kept = [];
        foreach (UsageDay day in _days.Values)
        {
            if (string.Equals(day.ProviderId, providerId, StringComparison.Ordinal)
                && day.Day >= from
                && day.Day <= to)
            {
                kept.Add(day);
            }
        }

        kept.Sort((left, right) => left.Day.CompareTo(right.Day));
        return kept;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Keeps the precedence the interface states: an observed day replaces anything, a
    /// backfilled one only fills a gap or replaces another backfilled day.
    /// </remarks>
    public ValueTask UpsertDaysAsync(IReadOnlyList<UsageDay> days, CancellationToken ct)
    {
        foreach (UsageDay day in days)
        {
            if (!_days.TryGetValue((day.ProviderId, day.Day), out UsageDay? stored)
                || day.Source == UsageDaySource.Observed
                || stored.Source == UsageDaySource.Backfilled)
            {
                _days[(day.ProviderId, day.Day)] = day;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Rolling days up belongs to the composition root's maintenance pass, not to a view
    /// model, so nothing here ever calls it: it writes nothing and returns zero.
    /// </remarks>
    public ValueTask<int> RollUpDaysAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        ValueTask.FromResult(0);

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken ct)
    {
        Clears++;
        _samples.Clear();
        _days.Clear();
        return ValueTask.CompletedTask;
    }
}
