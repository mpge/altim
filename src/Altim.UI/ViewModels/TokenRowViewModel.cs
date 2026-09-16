using Altim.UI.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// One line of a provider's token breakdown.
/// </summary>
/// <remarks>
/// A provider reports some of the four counts and not others, so a row whose count is null says
/// so in words rather than showing a zero.
/// </remarks>
public sealed class TokenRowViewModel : ObservableObject
{
    /// <summary>Initializes a token row.</summary>
    /// <param name="label">The count's name, such as <c>Input</c>.</param>
    /// <param name="value">The count, or null when the provider does not report it.</param>
    public TokenRowViewModel(string label, long? value)
    {
        ArgumentNullException.ThrowIfNull(label);

        Label = label;
        ValueText = value is { } count ? UsageFormat.Count(count) : UsageFormat.MetricUnavailable;
        IsReported = value is not null;
    }

    /// <summary>The count's name.</summary>
    public string Label { get; }

    /// <summary>The count, or the unavailable sentence.</summary>
    public string ValueText { get; }

    /// <summary>Whether the count was reported.</summary>
    public bool IsReported { get; }
}
