using Altim.UI.Formatting;

namespace Altim.UI.ViewModels;

/// <summary>
/// The one reading the tray panel leads with: the window nearest its ceiling, the figure, the
/// name of the window it belongs to and when that window resets.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot, like <see cref="MetricViewModel"/>: the panel rebuilds it on every reading
/// rather than mutating it, which is why nothing here is settable.
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

    /// <summary>The window and its provider: <c>Session (Claude Code)</c>.</summary>
    public string SourceLabel { get; }

    /// <summary>When the window rolls over: <c>Resets in 2h 14m</c>.</summary>
    public string ResetText { get; }
}
