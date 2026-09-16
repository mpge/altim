using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// One line of the popup's resets section.
/// </summary>
/// <remarks>
/// Only a window that actually reports a reset instant becomes a row. A window with no instant
/// is left out entirely rather than shown with a guessed time.
/// </remarks>
public sealed class ResetRowViewModel : ObservableObject
{
    /// <summary>Initializes a reset row.</summary>
    /// <param name="metricLabel">The window's label, such as <c>Session</c>.</param>
    /// <param name="providerName">The provider the window belongs to.</param>
    /// <param name="remainingText">The time remaining, such as <c>2h 14m</c>.</param>
    /// <param name="clockText">The reset instant as a local clock time, such as <c>8:00 PM</c>.</param>
    public ResetRowViewModel(string metricLabel, string providerName, string remainingText, string? clockText)
    {
        ArgumentNullException.ThrowIfNull(metricLabel);
        ArgumentNullException.ThrowIfNull(providerName);
        ArgumentNullException.ThrowIfNull(remainingText);

        MetricLabel = metricLabel;
        ProviderName = providerName;
        RemainingText = remainingText;
        ClockText = clockText;
    }

    /// <summary>The window's label.</summary>
    public string MetricLabel { get; }

    /// <summary>The provider the window belongs to.</summary>
    public string ProviderName { get; }

    /// <summary>The time remaining.</summary>
    public string RemainingText { get; }

    /// <summary>The reset instant as a local clock time, or null when it cannot be shown.</summary>
    public string? ClockText { get; }
}
