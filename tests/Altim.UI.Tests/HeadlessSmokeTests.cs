using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Proves the headless host starts, runs a test on the Avalonia dispatcher thread and
/// completes a layout pass. Everything that renders is tested against this harness.
/// </summary>
public sealed class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void HeadlessWindowLaysOut()
    {
        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            Content = new TextBlock { Text = "Not reported by this provider" },
        };

        window.Show();

        Assert.True(window.IsVisible);
        Assert.True(window.Bounds.Width > 0);
        Assert.True(window.Bounds.Height > 0);
    }
}
