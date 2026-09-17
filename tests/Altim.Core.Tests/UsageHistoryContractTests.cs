using Altim.Core.Abstractions;
using Altim.Core.Models;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The contract a chart caller reads history through, pinned without a database.
/// </summary>
/// <remarks>
/// History rows are written only when a value changes, so an empty range does not mean
/// there is nothing to draw: it means nothing moved. A caller therefore reads the
/// carry-in and the range together, and the two must never describe the same sample
/// twice. Both rules live in the interface rather than in one implementation of it, so
/// they are checked here, against the smallest implementation that can hold samples at
/// all.
/// </remarks>
public sealed class UsageHistoryContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AQuietWindowHasNoRangeButStillHasAValue()
    {
        IUsageHistoryService history = new InMemoryHistory(
            Sample("five_hour", Now.AddHours(-25), 41.5));

        Assert.Empty(await history.GetRangeAsync("claude", Now.AddHours(-24), Now, Ct));

        UsageSample carriedIn = Assert.Single(
            await history.GetLatestBeforeAsync("claude", Now.AddHours(-24), Ct));

        Assert.Equal(41.5, carriedIn.UsedPercent);
    }

    [Fact]
    public async Task TheCarryInAndTheRangeNeverHoldTheSameSample()
    {
        DateTimeOffset edge = Now.AddHours(-24);

        IUsageHistoryService history = new InMemoryHistory(
            Sample("five_hour", edge.AddMinutes(-1), 10),
            Sample("five_hour", edge, 20),
            Sample("five_hour", edge.AddMinutes(1), 30));

        IReadOnlyList<UsageSample> carriedIn =
            await history.GetLatestBeforeAsync("claude", edge, Ct);
        IReadOnlyList<UsageSample> inRange = await history.GetRangeAsync("claude", edge, Now, Ct);

        Assert.Equal(10d, Assert.Single(carriedIn).UsedPercent);
        Assert.Equal(new double?[] { 20d, 30d }, inRange.Select(s => s.UsedPercent).ToArray());
    }

    [Fact]
    public async Task NothingRecordedAtAllIsTheOnlyEmptyThatMeansEmpty()
    {
        IUsageHistoryService history = new InMemoryHistory();

        Assert.Empty(await history.GetRangeAsync("claude", Now.AddHours(-24), Now, Ct));
        Assert.Empty(await history.GetLatestBeforeAsync("claude", Now.AddHours(-24), Ct));
    }

    private static UsageSample Sample(string metricKey, DateTimeOffset at, double? percent)
        => new("claude", metricKey, at, percent, null, null, null);

    /// <summary>
    /// The smallest thing that can answer the interface: a list of samples. It exists to
    /// prove the contract is stated in terms of what a caller needs, not in terms of what
    /// one SQL schema happens to make easy.
    /// </summary>
    private sealed class InMemoryHistory(params UsageSample[] samples) : IUsageHistoryService
    {
        private readonly List<UsageSample> _samples = [.. samples];
        private readonly Dictionary<(string Provider, DateOnly Day), UsageDay> _days = [];

        public ValueTask RecordAsync(ProviderUsage usage, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(usage);

            if (usage.Status != ProviderStatus.Error)
            {
                foreach (UsageMetric metric in usage.Metrics)
                {
                    _samples.Add(new UsageSample(usage.ProviderId, metric.Key,
                                                 usage.LastRefreshed ?? DateTimeOffset.UnixEpoch,
                                                 metric.IsUsedPercentReported ? metric.UsedPercent : null,
                                                 metric.Window?.Length, metric.Window?.ResetsAt,
                                                 usage.Tokens));
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<UsageSample>> GetRangeAsync(
            string providerId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<UsageSample>>(
                [.. _samples.Where(s => s.ProviderId == providerId
                                        && s.CapturedAt >= from && s.CapturedAt < to)
                            .OrderBy(s => s.CapturedAt)]);

        public ValueTask<IReadOnlyList<UsageSample>> GetLatestBeforeAsync(
            string providerId, DateTimeOffset at, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<UsageSample>>(
                [.. _samples.Where(s => s.ProviderId == providerId && s.CapturedAt < at)
                            .GroupBy(s => s.MetricKey, StringComparer.Ordinal)
                            .OrderBy(group => group.Key, StringComparer.Ordinal)
                            .Select(group => group.MaxBy(s => s.CapturedAt)!)]);

        public ValueTask<IReadOnlyList<UsageDay>> GetDaysAsync(
            string providerId, DateOnly from, DateOnly to, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<UsageDay>>(
                [.. _days.Values.Where(d => d.ProviderId == providerId
                                            && d.Day >= from && d.Day <= to)
                                .OrderBy(d => d.Day)]);

        public ValueTask UpsertDaysAsync(IReadOnlyList<UsageDay> days, CancellationToken ct)
        {
            foreach (UsageDay day in days)
            {
                // Observed beats backfilled: a backfill fills gaps and never rewrites a day
                // Altim watched for itself.
                if (!_days.TryGetValue((day.ProviderId, day.Day), out UsageDay? stored)
                    || day.Source == UsageDaySource.Observed
                    || stored.Source == UsageDaySource.Backfilled)
                {
                    _days[(day.ProviderId, day.Day)] = day;
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken ct)
        {
            _samples.Clear();
            _days.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
