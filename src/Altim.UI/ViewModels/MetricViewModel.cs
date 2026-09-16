using Altim.Core.Models;
using Altim.Core.Notifications;
using Altim.Core.Settings;
using Altim.Core.Usage;
using Altim.UI.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// One metric row: a label, a figure, a meter and an optional caption.
/// </summary>
/// <remarks>
/// A row is a snapshot. Its owner rebuilds the collection on every reading rather than mutating
/// rows in place, which is why nothing here is settable. <see cref="Value"/> stays
/// <see langword="null"/> when the provider does not report a percentage, so the meter renders
/// its unavailable outline instead of an empty track that would read as a zero.
/// </remarks>
public sealed class MetricViewModel : ObservableObject
{
    /// <summary>Initializes a row from one reported metric.</summary>
    /// <param name="metric">The metric as the provider reported it.</param>
    /// <param name="settings">Supplies the threshold the meter ticks at.</param>
    /// <param name="timeProvider">The clock the reset caption is measured against.</param>
    /// <param name="isPrimary">Whether this is the first metric on its surface, which sets the figure size.</param>
    public MetricViewModel(
        UsageMetric metric,
        AltimSettings settings,
        TimeProvider timeProvider,
        bool isPrimary = false)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Key = metric.Key;
        Label = string.IsNullOrWhiteSpace(metric.Label)
            ? LimitWindowClassifier.Label(metric.Window) ?? metric.Key
            : metric.Label;
        IsPrimary = isPrimary;
        Value = UsagePercent.Normalise(metric.UsedPercent);
        PercentText = UsageFormat.Percent(Value);
        IsReported = PercentText is not null;
        Threshold = ThresholdEvaluator.ThresholdFor(settings, metric.Window);
        IsAboveThreshold = Value is { } value && value >= Threshold;
        ResetsAt = metric.Window?.ResetsAt;
        RemainingText = UsageFormat.Remaining(metric.Window, timeProvider);
        ResetText = UsageFormat.ResetsIn(metric.Window, timeProvider);
        ResetClockText = UsageFormat.ClockTime(metric.Window?.ResetsAt);
        WindowText = LimitWindowClassifier.Label(metric.Window);
        IsBestEffort = metric.Confidence == MetricConfidence.BestEffort;
    }

    /// <summary>The provider's own key for this metric.</summary>
    public string Key { get; }

    /// <summary>The label shown to the left of the figure.</summary>
    public string Label { get; }

    /// <summary>Whether this row carries the large figure on its surface.</summary>
    public bool IsPrimary { get; }

    /// <summary>The normalised percentage, or null when the provider does not report one.</summary>
    public double? Value { get; }

    /// <summary>The figure, or null when the provider does not report one.</summary>
    public string? PercentText { get; }

    /// <summary>Whether a figure exists to show.</summary>
    public bool IsReported { get; }

    /// <summary>Shown in place of the figure when nothing is reported.</summary>
    public string UnavailableText => UsageFormat.MetricUnavailable;

    /// <summary>The percentage the meter's threshold tick sits at.</summary>
    public double Threshold { get; }

    /// <summary>Whether the label turns to the warning colour.</summary>
    public bool IsAboveThreshold { get; }

    /// <summary>The reset instant itself, or null when none is reported.</summary>
    public DateTimeOffset? ResetsAt { get; }

    /// <summary>The bare time remaining, such as <c>2h 14m</c>, or null when none is reported.</summary>
    public string? RemainingText { get; }

    /// <summary>The reset caption, or null when no reset instant is reported.</summary>
    public string? ResetText { get; }

    /// <summary>Whether a reset caption exists.</summary>
    public bool HasReset => ResetText is not null;

    /// <summary>The reset instant as a local clock time, or null when none is reported.</summary>
    public string? ResetClockText { get; }

    /// <summary>The window's own name, such as <c>Session</c>, or null when it has none.</summary>
    public string? WindowText { get; }

    /// <summary>Whether the provider flagged this number as best effort rather than documented.</summary>
    public bool IsBestEffort { get; }
}
