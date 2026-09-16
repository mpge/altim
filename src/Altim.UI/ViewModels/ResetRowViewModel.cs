namespace Altim.UI.ViewModels;

/// <summary>
/// One line of the tray panel's resets section: one provider, and the next window of its
/// that rolls over.
/// </summary>
/// <remarks>
/// A provider reports several windows and the panel has room for one line each, so the line
/// carries the soonest of them. The rest are on the provider's own page, which is where a
/// person goes to read a provider in full. Only a window that actually reports a reset
/// instant becomes a row: a window with no instant is left out entirely rather than shown
/// with a time nobody reported.
/// </remarks>
/// <param name="ProviderName">The provider the window belongs to.</param>
/// <param name="RemainingText">The time remaining, such as <c>5h 12m</c>.</param>
public sealed record ResetRowViewModel(string ProviderName, string RemainingText)
{
    /// <summary>The provider's name as it reads beside the time: <c>(Claude Code)</c>.</summary>
    public string ProviderLabel => string.Concat("(", ProviderName, ")");
}
