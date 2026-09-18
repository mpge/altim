using Altim.UI.Formatting;

namespace Altim.UI.ViewModels;

/// <summary>
/// The one reading a surface leads with: the window nearest its ceiling, the figure, the
/// name of the window it belongs to and when that window resets.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot, like <see cref="MetricViewModel"/>: the surface rebuilds it on every reading
/// rather than mutating it, which is why nothing here is settable.
/// </para>
/// <para>
/// <b>Two surfaces lead with one.</b> The tray panel's dial carries the highest window any
/// provider reports; an Overview card's dial carries the highest window <i>that provider</i>
/// reports. The difference is the set the choice is made over, not the rule, so the rule lives
/// here as <see cref="Beats"/> and <see cref="Nearest"/> rather than once per surface. Two
/// copies of a tie break drift, and the tie break is the part nobody notices has drifted.
/// </para>
/// <para>
/// <b>The window is always named, and always named with its provider.</b> One window out of
/// several is on the dial, and a figure that does not say which window it measures is a
/// figure nobody can act on. The provider goes in parentheses after the window, which is the
/// form this panel already uses in its resets section.
/// </para>
/// <para>
/// A window with no percentage keeps its name and its reset time and shows an em dash for the
/// figure, because an unreported reading is not a zero and the dial draws it as an outline
/// rather than a sweep sitting at the bottom of the scale. A window with no reported reset
/// instant shows the em dash too, for the same reason.
/// </para>
/// </remarks>
public sealed class HeadlineReadingViewModel
{
    /// <summary>Builds the headline from one metric and the provider that reported it.</summary>
    /// <param name="metric">The metric the panel is leading with.</param>
    /// <param name="providerName">The provider's display name, never its identifier.</param>
    public HeadlineReadingViewModel(MetricViewModel metric, string providerName)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentException.ThrowIfNullOrEmpty(providerName);

        Value = metric.Value;
        Threshold = metric.Threshold;
        IsAboveThreshold = metric.IsAboveThreshold;
        IsReported = metric.IsReported;
        PercentText = metric.PercentText ?? UsageFormat.Unknown;
        WindowLabel = metric.Label;
        SourceLabel = string.Concat(metric.Label, " (", providerName, ")");
        ResetText = metric.ResetText
            ?? string.Concat(UsageFormat.ResetsLabel, " ", UsageFormat.Unknown);
    }

    /// <summary>The level the dial sweeps to, or null when the provider reports none.</summary>
    public double? Value { get; }

    /// <summary>The level the dial's index stands at.</summary>
    public double Threshold { get; }

    /// <summary>Whether the level has reached the threshold, which turns the name.</summary>
    public bool IsAboveThreshold { get; }

    /// <summary>Whether a figure exists to show.</summary>
    public bool IsReported { get; }

    /// <summary>The figure inside the dial, or an em dash when nothing is reported.</summary>
    public string PercentText { get; }

    /// <summary>
    /// The window on its own: <c>Session</c>. What a surface that has already named the
    /// provider prints under the dial.
    /// </summary>
    public string WindowLabel { get; }

    /// <summary>The window and its provider: <c>Session (Claude Code)</c>.</summary>
    public string SourceLabel { get; }

    /// <summary>When the window rolls over: <c>Resets in 2h 14m</c>.</summary>
    public string ResetText { get; }

    /// <summary>
    /// The window nearest its ceiling out of a set: the one the dial over that set shows.
    /// </summary>
    /// <param name="metrics">The windows to choose between.</param>
    /// <returns>The winner, or null when the set is empty.</returns>
    /// <remarks>
    /// <para>
    /// <b>Ranked by the raw level</b> rather than by how near each window is to its own
    /// threshold. Every window is drawn against one shared scale, which is the whole reason
    /// two readings can be compared by eye; ranking them by a ratio to a per-window alert
    /// level would order them by something nobody can see on that scale.
    /// </para>
    /// <para>
    /// A window with no percentage is never chosen over one that has a figure, but when
    /// nothing in the set reports one the first window is still returned: the dial draws its
    /// unavailable face and the figure is an em dash, which says "nothing was reported for
    /// this" rather than leaving the surface headed by nothing.
    /// </para>
    /// </remarks>
    public static MetricViewModel? Nearest(IEnumerable<MetricViewModel> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        MetricViewModel? best = null;
        foreach (MetricViewModel metric in metrics)
        {
            if (best is null || Beats(metric, best))
            {
                best = metric;
            }
        }

        return best;
    }

    /// <summary>Whether one window should be on the dial ahead of another.</summary>
    /// <param name="candidate">The window being considered.</param>
    /// <param name="holder">The window currently holding the dial.</param>
    /// <returns>True when the candidate takes it.</returns>
    /// <remarks>
    /// Level for level, the window that rolls over first is the one reached first, and a
    /// window reporting no reset instant never displaces one that does. Past that the caller's
    /// own order decides, so the same readings always pick the same window.
    /// </remarks>
    public static bool Beats(MetricViewModel candidate, MetricViewModel holder)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(holder);

        if (candidate.Value is not { } level)
        {
            return false;
        }

        if (holder.Value is not { } held)
        {
            return true;
        }

        if (level > held)
        {
            return true;
        }

        if (level < held)
        {
            return false;
        }

        return candidate.ResetsAt is { } instant
            && (holder.ResetsAt is not { } best || instant < best);
    }
}
