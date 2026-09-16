using Altim.Core.Models;
using Altim.Providers.Limits;

namespace Altim.Providers.Codex.Limits;

/// <summary>
/// Turns normalised Codex windows into the metrics the UI renders.
/// </summary>
/// <remarks>
/// <para>
/// One metric is emitted per limit family per window the family actually reports, and the
/// label is derived from the window's length rather than the slot it arrived in. That is
/// the whole point: an implementation that assumed "primary is the five-hour window" would
/// have mislabelled a weekly allowance as a session allowance on a real account in 2026-09.
/// </para>
/// <para>
/// A family that stops reporting a window produces no metric for it, so the meter
/// disappears instead of holding a stale number on screen.
/// </para>
/// <para>
/// Keys are storage keys. They use the <em>normalised</em> window length, so a limit that
/// reports 10,079 minutes on one poll and 10,080 on the next keeps one continuous history
/// rather than splitting into two series.
/// </para>
/// </remarks>
public static class CodexMetricFactory
{
    /// <summary>
    /// Builds the metrics for a snapshot.
    /// </summary>
    /// <param name="windows">The normalised windows.</param>
    /// <param name="confidence">
    /// How much weight these readings carry. Live app-server readings are
    /// <see cref="MetricConfidence.BestEffort"/>: the interface is experimental and
    /// undocumented, so it is never presented as a published figure.
    /// </param>
    /// <param name="now">
    /// The current instant, used to drop windows that have already rolled over. Pass
    /// <see langword="null"/> to keep every window, which is what a caller reading a
    /// snapshot for its own sake wants.
    /// </param>
    /// <returns>
    /// The metrics, ordered by family and then by window length, with duplicates on the
    /// same key collapsed to the first occurrence.
    /// </returns>
    /// <remarks>
    /// A window whose reset instant has passed describes a period that is over. Its
    /// percentage was true of that period and is not true of the one running now, so no
    /// metric is emitted: a recovered snapshot from last week must not put an 85 per cent
    /// meter on screen on Monday morning.
    /// </remarks>
    public static IReadOnlyList<UsageMetric> Build(
        IReadOnlyList<CodexLimitWindow> windows,
        MetricConfidence confidence,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var metrics = new List<UsageMetric>(windows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (CodexLimitWindow window in windows.OrderBy(static w => w.LimitId, StringComparer.Ordinal)
                     .ThenBy(static w => LimitWindowClassifier.Normalize(w.WindowMinutes)))
        {
            if (HasPassed(window.ResetsAt, now))
            {
                continue;
            }

            string key = BuildKey(window);
            if (!seen.Add(key))
            {
                continue;
            }

            metrics.Add(new UsageMetric(
                key,
                BuildLabel(window),
                window.UsedPercent,
                new LimitWindow(TimeSpan.FromMinutes(LimitWindowClassifier.Normalize(window.WindowMinutes)), window.ResetsAt),
                confidence));
        }

        return metrics;
    }

    /// <summary>
    /// True when a reported reset instant is at or before <paramref name="now"/>.
    /// </summary>
    /// <param name="resetsAt">The reported reset instant, or <see langword="null"/>.</param>
    /// <param name="now">The current instant, or <see langword="null"/> to disable the check.</param>
    public static bool HasPassed(DateTimeOffset? resetsAt, DateTimeOffset? now) =>
        resetsAt is { } instant && now is { } current && instant <= current;

    /// <summary>
    /// The storage key for a window, for example <c>codex:10080</c>.
    /// </summary>
    /// <param name="window">The window.</param>
    public static string BuildKey(CodexLimitWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.LimitId + ":" + LimitWindowClassifier.KeyFragment(window.WindowMinutes);
    }

    /// <summary>
    /// The display label for a window: its length, qualified by the family when the account
    /// has more than the default one.
    /// </summary>
    /// <param name="window">The window.</param>
    public static string BuildLabel(CodexLimitWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        string duration = LimitWindowClassifier.Label(window.WindowMinutes);
        if (string.Equals(window.LimitId, CodexRateLimitParser.DefaultLimitId, StringComparison.Ordinal))
        {
            return duration;
        }

        string family = window.LimitName ?? window.LimitId;
        return family + " " + duration;
    }
}
