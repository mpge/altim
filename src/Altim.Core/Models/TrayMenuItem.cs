namespace Altim.Core.Models;

/// <summary>
/// One entry in the native tray menu. Flat by design: every platform renders a flat
/// list identically, and nested menus do not.
/// </summary>
/// <param name="Id">
/// Identifier reported back through the host menu event. An empty string marks a
/// separator; see <see cref="Separator"/>.
/// </param>
/// <param name="Label">
/// The text shown, in sentence case. Ignored for a separator.
/// </param>
/// <param name="IsEnabled">
/// False renders the entry greyed out and unclickable.
/// </param>
/// <param name="IsChecked">
/// True renders the platform check mark against the entry.
/// </param>
public sealed record TrayMenuItem(string Id, string Label, bool IsEnabled = true, bool IsChecked = false)
{
    /// <summary>
    /// The separator entry. Hosts render it as a rule and never raise an event for it.
    /// </summary>
    public static TrayMenuItem Separator { get; } = new(string.Empty, string.Empty, IsEnabled: false);

    /// <summary>True when this entry is the separator rather than a command.</summary>
    public bool IsSeparator => Id.Length == 0;
}
