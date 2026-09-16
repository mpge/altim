namespace Altim.UI.ViewModels;

/// <summary>
/// A page the dashboard sidebar can select.
/// </summary>
/// <remarks>
/// A page loads when it is shown rather than when the window opens, so selecting Settings does
/// not cost a history query and opening the window does not cost a session scan.
/// </remarks>
public interface IDashboardPage
{
    /// <summary>The page's name, as it reads in the sidebar.</summary>
    string Title { get; }

    /// <summary>Loads whatever the page shows. Never throws; a failure becomes page copy.</summary>
    /// <param name="ct">Cancels the load.</param>
    Task LoadAsync(CancellationToken ct);
}
