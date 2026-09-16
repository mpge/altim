using System.Globalization;
using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// Combines the readings of several providers into one overview. Pure: no clock, no state,
/// same input gives the same output.
/// </summary>
public static class UsageAggregator
{
    /// <summary>
    /// Reduces a set of readings to the worst metric, an overall status and a summed set of
    /// token totals.
    /// </summary>
    /// <param name="usages">
    /// One reading per provider. An empty set produces <see cref="UsageOverview.Empty"/>.
    /// </param>
    /// <returns>
    /// The overview. Nothing in it is defaulted to zero: a metric nobody reported stays
    /// <see langword="null"/>, and so does a token component nobody reported.
    /// </returns>
    public static UsageOverview Aggregate(IEnumerable<ProviderUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(usages);

        List<ProviderUsage> readings = [.. usages];
        if (readings.Count == 0)
        {
            return UsageOverview.Empty;
        }

        string? worstProviderId = null;
        UsageMetric? worstMetric = null;
        double worstPercent = double.NegativeInfinity;

        long? input = null;
        long? output = null;
        long? cacheRead = null;
        long? cacheWrite = null;

        ProviderStatus overall = ProviderStatus.Active;
        bool statusSeen = false;

        foreach (ProviderUsage usage in readings)
        {
            if (!statusSeen || Severity(usage.Status) > Severity(overall))
            {
                overall = usage.Status;
                statusSeen = true;
            }

            // A failed reading contributes to the status line and nothing else: every
            // metric on it is unavailable, not zero.
            if (usage.Status == ProviderStatus.Error)
            {
                continue;
            }

            foreach (UsageMetric metric in usage.Metrics)
            {
                if (UsagePercent.Normalise(metric.UsedPercent) is not { } percent)
                {
                    continue;
                }

                if (percent > worstPercent)
                {
                    worstPercent = percent;
                    worstMetric = metric.UsedPercent == percent ? metric : metric with { UsedPercent = percent };
                    worstProviderId = usage.ProviderId;
                }
            }

            if (usage.Tokens is { } tokens)
            {
                input = Add(input, tokens.Input);
                output = Add(output, tokens.Output);
                cacheRead = Add(cacheRead, tokens.CacheRead);
                cacheWrite = Add(cacheWrite, tokens.CacheWrite);
            }
        }

        TokenTotals? totals = input is null && output is null && cacheRead is null && cacheWrite is null
            ? null
            : new TokenTotals(input, output, cacheRead, cacheWrite);

        return new UsageOverview(
            worstProviderId,
            worstMetric,
            overall,
            DescribeStatus(readings),
            totals,
            readings.Count);
    }

    /// <summary>
    /// The one sentence describing where the integrations stand as a whole.
    /// </summary>
    /// <param name="usages">One reading per provider.</param>
    /// <returns>Never empty. Describes the least healthy thing that is true.</returns>
    public static string DescribeStatus(IEnumerable<ProviderUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(usages);

        List<ProviderUsage> readings = [.. usages];
        if (readings.Count == 0)
        {
            return UsageOverview.Empty.StatusLine;
        }

        List<string> failed = Ids(readings, ProviderStatus.Error);
        if (failed.Count == 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{failed[0]} unavailable");
        }

        if (failed.Count > 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{failed.Count} providers unavailable");
        }

        List<string> missing = Ids(readings, ProviderStatus.NotDetected);
        if (missing.Count == readings.Count)
        {
            return "No providers detected";
        }

        if (missing.Count == 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{missing[0]} not detected");
        }

        if (missing.Count > 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{missing.Count} providers not detected");
        }

        List<string> unknown = Ids(readings, ProviderStatus.Unknown);
        if (unknown.Count == readings.Count)
        {
            return "Waiting for the first reading";
        }

        return unknown.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Waiting for {unknown[0]}")
            : "All providers operational";
    }

    /// <summary>
    /// How bad a status is, where a larger number is worse. Used to pick the overall
    /// status: the worst one present wins.
    /// </summary>
    /// <param name="status">The status to rank.</param>
    public static int Severity(ProviderStatus status) => status switch
    {
        ProviderStatus.Error => 5,
        ProviderStatus.NotDetected => 4,
        ProviderStatus.Unknown => 3,
        ProviderStatus.Detected => 2,
        ProviderStatus.Idle => 1,
        ProviderStatus.Active => 0,
        _ => 3,
    };

    private static List<string> Ids(List<ProviderUsage> readings, ProviderStatus status)
    {
        List<string> ids = [];
        foreach (ProviderUsage usage in readings)
        {
            if (usage.Status == status)
            {
                ids.Add(usage.ProviderId);
            }
        }

        return ids;
    }

    private static long? Add(long? running, long? reported) => (running, reported) switch
    {
        (null, null) => null,
        (null, { } only) => only,
        ({ } only, null) => only,
        ({ } a, { } b) => a + b,
    };
}
