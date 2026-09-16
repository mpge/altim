using Altim.UI.ViewModels;
using Avalonia.Controls;

namespace Altim.UI.Views;

/// <summary>
/// The window the tray panel lives in.
/// </summary>
/// <remarks>
/// Positioning, showing, hiding and dismissal belong to the composition root: the anchor comes
/// from a platform service this project cannot reference, and the rules for what counts as a
/// deactivation differ per operating system. This type is only the surface. The arithmetic the
/// composition root positions it with is <see cref="PopupPlacement"/>, which is here rather than
/// there because it is a pure function over rectangles and belongs with the geometry it is about.
/// </remarks>
public partial class PopupWindow : Window
{
    /// <summary>Initializes the window without a view model.</summary>
    public PopupWindow() => InitializeComponent();

    /// <summary>Initializes the window over a view model.</summary>
    /// <param name="viewModel">The panel to show.</param>
    public PopupWindow(PopupViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
    }
}
