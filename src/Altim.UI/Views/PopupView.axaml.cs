using Avalonia.Controls;

namespace Altim.UI.Views;

/// <summary>
/// The tray panel's contents, separated from the window so the panel can be previewed and
/// tested without a top level.
/// </summary>
public partial class PopupView : UserControl
{
    /// <summary>Initializes the panel.</summary>
    public PopupView() => InitializeComponent();
}
