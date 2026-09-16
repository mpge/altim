namespace Altim.Core.Abstractions;

/// <summary>
/// Identifies the native menu entry the user picked.
/// </summary>
/// <param name="itemId">
/// The <c>Id</c> of the entry that was picked. Never empty, because separators raise
/// no event.
/// </param>
public sealed class TrayMenuItemInvokedEventArgs(string itemId) : EventArgs
{
    /// <summary>The identifier of the entry the user picked.</summary>
    public string ItemId { get; } = itemId;
}
