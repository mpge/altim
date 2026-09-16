namespace Altim.UI.ViewModels;

/// <summary>
/// One window on the tray panel's single line per provider: <c>5h 42%</c>.
/// </summary>
/// <remarks>
/// The panel has one line to say everything a provider is reporting, so the window is named
/// by its length rather than by its label - <c>5h</c> rather than <c>Session</c> - and the
/// figure sits straight after it. The separator is carried by the row rather than drawn
/// between rows, because the first item must not have one and an items control has no way to
/// say so.
/// </remarks>
/// <param name="Label">The window's shorthand, such as <c>5h</c>.</param>
/// <param name="ValueText">The figure, such as <c>42%</c>. Never null: a window with nothing
/// to report is left out of the line rather than shown with a placeholder.</param>
/// <param name="ShowsSeparator">Whether a middle dot is drawn before this item.</param>
public sealed record CompactMetricViewModel(string Label, string ValueText, bool ShowsSeparator);
