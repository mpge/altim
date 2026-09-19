using Altim.Core.Monitoring;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Whether launching puts a window on screen.
/// </summary>
/// <remarks>
/// The defect this covers: a fresh install opened nothing at all. "Start minimised" defaults
/// to on, so the composition root honoured it on the very first launch, and this process has
/// no main window by design. Windows files a tray icon nobody has seen before into the
/// overflow, so the installer finished and the whole application was behind a chevron the
/// user had no reason to click.
/// </remarks>
public sealed class StartupWindowsTests
{
    /// <summary>
    /// A first run opens the dashboard however "start minimised" is set, because at that
    /// point it is a default rather than anybody's decision.
    /// </summary>
    /// <param name="startMinimised">The setting, which must not matter here.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFirstRunOpensTheDashboardWhateverTheSettingSays(bool startMinimised)
    {
        Assert.True(StartupWindows.ShouldOpenDashboard(firstRun: true, startMinimised));
    }

    /// <summary>After that the setting is the answer, because by then somebody chose it.</summary>
    [Fact]
    public void AfterTheFirstRunTheSettingDecides()
    {
        Assert.False(StartupWindows.ShouldOpenDashboard(firstRun: false, startMinimised: true));
        Assert.True(StartupWindows.ShouldOpenDashboard(firstRun: false, startMinimised: false));
    }

    /// <summary>
    /// The mark is a scalar under the startup prefix, not one of the settings the page
    /// offers. Nothing should present "have you seen this yet" as a preference.
    /// </summary>
    [Fact]
    public void TheMarkIsAScalarRatherThanASetting()
    {
        Assert.Equal("startup.first_run_shown", StartupWindows.FirstRunKey);
    }
}
