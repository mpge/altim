using System.Globalization;
using Altim.Core.Models;

namespace Altim.Providers;

/// <summary>
/// Shared helpers for comparing and describing readings.
/// </summary>
public static class UsageReadings
{
    /// <summary>
    /// True when two readings say the same thing.
    /// </summary>
    /// <param name="previous">The last reading, or <see langword="null"/> when there was none.</param>
    /// <param name="current">The new reading.</param>
    /// <remarks>
    /// <see cref="ProviderUsage.LastRefreshed"/> is ignored, because it changes on every
    /// poll and a provider that raised a change event every minute would keep the UI and
    /// the history table busy recording that nothing had happened. Record equality alone
    /// would not do: <see cref="ProviderUsage.Metrics"/> is a list, so two equal readings
    /// in different list instances compare unequal.
    /// </remarks>
    public static bool AreEquivalent(ProviderUsage? previous, ProviderUsage current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous is null)
        {
            return false;
        }

        if (!string.Equals(previous.ProviderId, current.ProviderId, StringComparison.Ordinal)
            || previous.Status != current.Status
            || !string.Equals(previous.StatusDetail, current.StatusDetail, StringComparison.Ordinal)
            || previous.Tokens != current.Tokens
            || previous.Metrics.Count != current.Metrics.Count)
        {
            return false;
        }

        for (int i = 0; i < current.Metrics.Count; i++)
        {
            if (previous.Metrics[i] != current.Metrics[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Describes how long ago an instant was, in words, for a staleness note.
    /// </summary>
    /// <param name="observedAt">When the data was produced.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>
    /// A phrase such as "4 minutes ago". "an unknown time ago" when
    /// <paramref name="observedAt"/> is <see langword="null"/>, because an unknown age is
    /// stated rather than rounded down to "just now".
    /// </returns>
    public static string DescribeAge(DateTimeOffset? observedAt, DateTimeOffset now)
    {
        if (observedAt is not { } observed)
        {
            return "an unknown time ago";
        }

        TimeSpan age = now - observed;
        if (age < TimeSpan.Zero)
        {
            // A clock that disagrees with the provider's. Do not claim the future.
            return "just now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Plural((long)age.TotalMinutes, "minute");
        }

        if (age < TimeSpan.FromDays(1))
        {
            return Plural((long)age.TotalHours, "hour");
        }

        return Plural((long)age.TotalDays, "day");
    }

    private static string Plural(long count, string unit)
    {
        string suffix = count == 1 ? string.Empty : "s";
        return string.Create(CultureInfo.InvariantCulture, $"{count} {unit}{suffix} ago");
    }
}
