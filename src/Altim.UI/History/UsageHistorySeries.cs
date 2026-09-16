using Altim.Core.Models;
using Altim.Core.Usage;

namespace Altim.UI.History;

/// <summary>
/// Turns stored samples into the evenly spaced values <c>UsageTape</c> draws.
/// </summary>
/// <remarks>
/// <para>
/// History is sparse on purpose: a sample is written only when a value changes, so a quiet
/// afternoon stores nothing at all and a range can open with no sample inside it while the level
/// is perfectly well known. Bucketing naively would either invent zeroes for the quiet hours or
/// scatter unconnected points across the plot.
/// </para>
/// <para>
/// So a bucket takes the last sample recorded at or before it, including the carry-in sample from
/// before the range, and holds that level forward, because a level does hold until something
/// changes it. The hold stops at the window's reset instant, or, when the provider never reported
/// one, at one window length after the sample was taken: past that point the window has certainly
/// rolled over and the level is genuinely unknown. A bucket with nothing to hold is null, and a
/// range where nothing can be held at all comes back empty, which is what makes the tape show its
/// sentence rather than a line along zero.
/// </para>
/// </remarks>
public static class UsageHistorySeries
{
    /// <summary>Picks the metric key a provider's line is drawn from.</summary>
    /// <param name="samples">Samples for one provider.</param>
    /// <returns>The chosen key, or null when there is nothing to choose from.</returns>
    /// <remarks>
    /// A provider reports several windows and the tape draws one line per provider, so the
    /// shortest window wins: it is the one that moves within a day and the one a user watches.
    /// Ties fall to the metric with the most samples, then to the key itself, so the choice is
    /// stable between refreshes.
    /// </remarks>
    public static string? SelectPrimaryMetricKey(IReadOnlyList<UsageSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        Dictionary<string, (double Window, int Count)> metrics = [];
        foreach (UsageSample sample in samples)
        {
            double window = sample.WindowLength is { } length && length > TimeSpan.Zero
                ? length.TotalMinutes
                : double.MaxValue;

            if (metrics.TryGetValue(sample.MetricKey, out (double Window, int Count) seen))
            {
                metrics[sample.MetricKey] = (Math.Min(seen.Window, window), seen.Count + 1);
                continue;
            }

            metrics[sample.MetricKey] = (window, 1);
        }

        string? bestKey = null;
        double bestWindow = double.MaxValue;
        int bestCount = 0;

        foreach ((string key, (double window, int count)) in metrics)
        {
            bool better = bestKey is null
                || window < bestWindow
                || (window == bestWindow && count > bestCount)
                || (window == bestWindow && count == bestCount && string.CompareOrdinal(key, bestKey) < 0);

            if (!better)
            {
                continue;
            }

            bestKey = key;
            bestWindow = window;
            bestCount = count;
        }

        return bestKey;
    }

    /// <summary>Filters samples down to one metric.</summary>
    /// <param name="samples">The samples to filter.</param>
    /// <param name="metricKey">The metric to keep.</param>
    public static IReadOnlyList<UsageSample> ForMetric(IReadOnlyList<UsageSample> samples, string metricKey)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(metricKey);

        List<UsageSample> kept = [];
        foreach (UsageSample sample in samples)
        {
            if (string.Equals(sample.MetricKey, metricKey, StringComparison.Ordinal))
            {
                kept.Add(sample);
            }
        }

        return kept;
    }

    /// <summary>Picks the primary metric and returns only its samples.</summary>
    /// <param name="samples">Samples for one provider.</param>
    public static IReadOnlyList<UsageSample> SelectPrimaryMetric(IReadOnlyList<UsageSample> samples) =>
        SelectPrimaryMetricKey(samples) is { } key ? ForMetric(samples, key) : [];

    /// <summary>Builds one line's values across a range.</summary>
    /// <param name="samples">
    /// The samples for a single metric. Samples from before <paramref name="from"/> are the
    /// carry-in and seed the left edge rather than being discarded.
    /// </param>
    /// <param name="from">The start of the range.</param>
    /// <param name="to">The end of the range.</param>
    /// <param name="buckets">How many points the line is drawn with.</param>
    /// <returns>
    /// One value per bucket, null where the level is unknown, or an empty list when nothing in
    /// the range can be drawn at all.
    /// </returns>
    public static IReadOnlyList<double?> Build(
        IReadOnlyList<UsageSample> samples,
        DateTimeOffset from,
        DateTimeOffset to,
        int buckets)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);

        if (to <= from)
        {
            return [];
        }

        List<UsageSample> usable = [];
        foreach (UsageSample sample in samples)
        {
            if (sample.CapturedAt <= to && UsagePercent.Normalise(sample.UsedPercent) is not null)
            {
                usable.Add(sample);
            }
        }

        if (usable.Count == 0)
        {
            return [];
        }

        usable.Sort(static (left, right) => left.CapturedAt.CompareTo(right.CapturedAt));

        TimeSpan step = (to - from) / buckets;
        double?[] values = new double?[buckets];
        int cursor = 0;
        int drawn = 0;
        UsageSample? held = null;

        for (int i = 0; i < buckets; i++)
        {
            DateTimeOffset start = from + (step * i);
            DateTimeOffset end = i == buckets - 1 ? to : from + (step * (i + 1));

            while (cursor < usable.Count && usable[cursor].CapturedAt <= end)
            {
                held = usable[cursor];
                cursor++;
            }

            if (held is null || (ExpiresAt(held) is { } expiry && expiry <= start))
            {
                values[i] = null;
                continue;
            }

            values[i] = UsagePercent.Normalise(held.UsedPercent);
            drawn++;
        }

        return drawn == 0 ? [] : values;
    }

    private static DateTimeOffset? ExpiresAt(UsageSample sample)
    {
        if (sample.ResetsAt is { } resetsAt)
        {
            return resetsAt;
        }

        // No reset instant was reported, so the only bound the sample carries is its own
        // window: one window length after it was taken, whatever it measured has rolled over.
        return sample.WindowLength is { } length && length > TimeSpan.Zero
            ? sample.CapturedAt + length
            : null;
    }
}
