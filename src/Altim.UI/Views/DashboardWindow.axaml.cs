using Altim.UI.ViewModels;
using Avalonia.Controls;

namespace Altim.UI.Views;

/// <summary>
/// The dashboard window. Constructed when it is opened and closed on dismiss, so its render
/// surfaces are released rather than held for a window nobody is looking at.
/// </summary>
public partial class DashboardWindow : Window
{
    /// <summary>Initializes the window without a view model.</summary>
    public DashboardWindow() => InitializeComponent();

    /// <summary>Initializes the window over a view model.</summary>
    /// <param name="viewModel">The dashboard to show.</param>
    public DashboardWindow(DashboardViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
    }
}
