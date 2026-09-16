using Avalonia.Controls;

namespace Altim.UI.Views;

/// <summary>
/// One metric row. Shared by every surface that shows a metric, so the shape cannot drift
/// between the popup and the dashboard.
/// </summary>
public partial class MetricRowView : UserControl
{
    /// <summary>Initializes the row.</summary>
    public MetricRowView() => InitializeComponent();
}
