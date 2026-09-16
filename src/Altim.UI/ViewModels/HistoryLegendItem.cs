namespace Altim.UI.ViewModels;

/// <summary>
/// One entry in the history tape's legend: a dot in the line's own weight and the provider's
/// name beside it.
/// </summary>
/// <remarks>
/// The tape has two line weights rather than a palette, because a history chart is not an
/// identity surface and DESIGN.md keeps colour for identity and status. So the legend has two
/// dot weights to match, and no provider accent reaches it.
/// </remarks>
/// <param name="Name">The provider's name.</param>
/// <param name="IsSecondary">Whether this line is drawn in the secondary weight.</param>
public sealed record HistoryLegendItem(string Name, bool IsSecondary);
