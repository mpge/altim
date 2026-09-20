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
/// <para>
/// A row is a snapshot of one reading. Its owner rebuilds the collection on every reading
/// rather than mutating rows in place, which is why nothing the provider reported is
/// settable. <see cref="Value"/> stays <see langword="null"/> when the provider does not
/// report a percentage, so the meter renders its unavailable outline instead of an empty
/// track that would read as a zero.
/// </para>
/// <para>
/// <b>The two countdowns are the exception, because they are not part of the reading.</b>
/// <see cref="RemainingText"/> and <see cref="ResetText"/> are the reported reset instant
/// measured against the clock, so they go out of date while the reading itself does not
/// change at all - and a provider announces a reading only when its value moved, which on a
/// quiet machine is never. Computed once in the constructor, they left the tray panel
/// reading "Resets in 2h 14m" for eight and a half minutes on the verification machine, with
/// the true figure at 2h 51m by the end of it. <see cref="RefreshCountdown"/> recomputes them
/// against the clock and says whether the words actually moved, so a surface can keep them
/// current without throwing its rows away and without writing when nothing has changed.
/// </para>
/// </remarks>
public sealed class MetricViewModel : ObservableObject
{
    private readonly TimeProvider _timeProvider;
    private string? _remainingText;
    private string? _resetText;

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

        _timeProvider = timeProvider;

        Key = metric.Key;
        Window = metric.Window;
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

        // Through the fields: a constructor has no listeners, and raising a change from one
        // is how a half-built object reaches a handler.
        _remainingText = UsageFormat.Remaining(metric.Window, timeProvider);
        _resetText = UsageFormat.ResetsIn(metric.Window, timeProvider);

        WindowText = LimitWindowClassifier.Label(metric.Window);
        IsBestEffort = metric.Confidence == MetricConfidence.BestEffort;
    }

    /// <summary>The provider's own key for this metric.</summary>
    public string Key { get; }

    /// <summary>The window the metric is measured over, or null when none was reported.</summary>
    public LimitWindow? Window { get; }

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
    /// <remarks>Measured against the clock by <see cref="RefreshCountdown"/>, not reported.</remarks>
    public string? RemainingText
    {
        get => _remainingText;
        private set => SetProperty(ref _remainingText, value);
    }

    /// <summary>The reset caption, or null when no reset instant is reported.</summary>
    /// <remarks>Measured against the clock by <see cref="RefreshCountdown"/>, not reported.</remarks>
    public string? ResetText
    {
        get => _resetText;
        private set => SetProperty(ref _resetText, value);
    }

    /// <summary>
    /// Whether a reset caption exists.
    /// </summary>
    /// <remarks>
    /// Fixed for the life of the row, which is why it raises nothing: both countdowns are
    /// null exactly when the window reports no reset instant, and the window a row was built
    /// from never changes. Only the words move.
    /// </remarks>
    public bool HasReset => ResetText is not null;

    /// <summary>The window's own name, such as <c>Session</c>, or null when it has none.</summary>
    public string? WindowText { get; }

    /// <summary>Whether the provider flagged this number as best effort rather than documented.</summary>
    public bool IsBestEffort { get; }

    /// <summary>
    /// Where this figure came from, when that is worth saying, and null when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is rule 2's mitigation and for a long time it did not exist:
    /// <see cref="IsBestEffort"/> was computed, exposed, and read by nothing, so a figure
    /// Altim had worked out of prose and one the vendor stated were byte for byte the same on
    /// screen. A reader cannot weigh a number they have not been told the provenance of.
    /// </para>
    /// <para>
    /// Null for a documented figure rather than a reassuring sentence. Most rows are
    /// documented, and a caption under every one of them would be noise that teaches a reader
    /// to stop looking — which would cost exactly the rows this exists for.
    /// </para>
    /// </remarks>
    public string? ConfidenceNotice => IsBestEffort ? UsageFormat.BestEffortFigure : null;

    /// <summary>
    /// Re-measures the reported reset instant against the clock.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when either countdown now reads differently, so a caller can
    /// leave the rest of its surface alone the rest of the time.
    /// </returns>
    /// <remarks>
    /// Nothing the provider reported is touched. The reset instant is the provider's; how
    /// far away it is, is the clock's, and this is the only part of a row that can go stale
    /// without a new reading.
    /// </remarks>
    public bool RefreshCountdown()
    {
        string? remaining = UsageFormat.Remaining(Window, _timeProvider);
        string? reset = UsageFormat.ResetsIn(Window, _timeProvider);

        if (string.Equals(remaining, RemainingText, StringComparison.Ordinal)
            && string.Equals(reset, ResetText, StringComparison.Ordinal))
        {
            return false;
        }

        RemainingText = remaining;
        ResetText = reset;
        return true;
    }
}
