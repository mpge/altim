using Altim.Core.Models;
using Altim.UI.Formatting;

namespace Altim.UI.ViewModels;

/// <summary>
/// A provider status as the interface is allowed to show it: one word and one 6px dot.
/// </summary>
/// <remarks>
/// Held as an immutable record that a view model replaces wholesale, so a status change raises
/// one property change and the bound row re-reads all of it at once. The three flags exist so a
/// view can pick the dot colour with a style class instead of a brush binding, which keeps the
/// colour resolving per theme variant.
/// </remarks>
/// <param name="Status">The status the provider reported.</param>
public sealed record StatusDisplay(ProviderStatus Status)
{
    /// <summary>The status before any reading has been taken.</summary>
    public static StatusDisplay Unknown { get; } = new(ProviderStatus.Unknown);

    /// <summary>The single word the status appears as.</summary>
    public string Word => UsageFormat.StatusWord(Status);

    /// <summary>Whether the dot reads as operational.</summary>
    public bool IsOk => Status is ProviderStatus.Active or ProviderStatus.Idle or ProviderStatus.Detected;

    /// <summary>Whether the dot reads as unreachable.</summary>
    public bool IsError => Status is ProviderStatus.Error;

    /// <summary>Whether the dot carries no status colour at all.</summary>
    public bool IsNeutral => !IsOk && !IsError;
}
