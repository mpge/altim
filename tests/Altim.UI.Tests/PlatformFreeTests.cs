using Altim.Core.Abstractions;
using Altim.Core.Settings;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Avalonia.Media;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// A view model must be constructible without Avalonia's rendering platform.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Geometry.Parse"/> resolves <c>IPlatformRenderInterface</c> from the locator, so
/// it throws in any process where the platform has not been initialised. That on its own would
/// be a contained failure. What made it a portfolio-wide one is where it was called from: a
/// static field initialiser. A type initialiser that throws is not retried - the CLR caches
/// the failure and rethrows <see cref="TypeInitializationException"/> from every later touch of
/// that type for the life of the process. So one view model built before a window existed took
/// out every test that went near the type afterwards, and it did it only on a runner whose test
/// ordering put that test first. It passed locally and failed in CI.
/// </para>
/// <para>
/// The fix is that geometry is never held by a view model and never parsed at type
/// initialisation: the paths are strings, they are parsed on first read, and the read happens
/// when a style is applied to a control that is already in a visual tree.
/// </para>
/// <para>
/// These are plain facts rather than <c>AvaloniaFact</c>s, deliberately: they must not be given
/// the platform they are checking the absence of a need for. They can still only catch the
/// regression when they happen to run before anything that initialises it, so the assertions
/// that hold whatever order the suite runs in are in <see cref="SourceShapeTests"/>: no view
/// model declares a geometry, and neither icon source holds a parsed one in a static field.
/// </para>
/// </remarks>
public sealed class PlatformFreeTests
{
    /// <summary>The whole dashboard builds with no application, no window and no renderer.</summary>
    [Fact]
    public void TheDashboardBuildsWithoutARenderingPlatform()
    {
        IUsageProvider[] providers =
        [
            new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude")),
            new FakeUsageProvider("codex", "Codex", Readings.Healthy("codex", 41d)),
        ];

        using var dashboard = new DashboardViewModel(
            providers,
            new FakeHistoryService(),
            new FakeSettingsStore(),
            new TestClock(Readings.Now));

        Assert.Equal(5, dashboard.Sections.Count);
        Assert.True(dashboard.Sections[1].IsProviderMark);
        Assert.True(dashboard.Sections[1].IsAnthropic);
    }

    /// <summary>So does the tray panel, with a reading applied to it.</summary>
    [Fact]
    public void TheTrayPanelBuildsWithoutARenderingPlatform()
    {
        var provider = new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude"));

        using var panel = new PopupViewModel(
            [provider],
            new TestClock(Readings.Now),
            AltimSettings.Default);

        panel.Providers[0].Apply(provider.Reading);

        Assert.Equal("All providers operational", panel.StatusLine);
        Assert.Equal(2, panel.Providers[0].CompactMetrics.Count);
        Assert.Equal("5h", panel.Providers[0].CompactMetrics[0].Label);
        Assert.Equal("62%", panel.Providers[0].CompactMetrics[0].ValueText);
        Assert.False(panel.Providers[0].CompactMetrics[0].ShowsSeparator);
        Assert.True(panel.Providers[0].CompactMetrics[1].ShowsSeparator);
    }

    /// <summary>
    /// The paths themselves are reachable, and reading one does not need the platform
    /// either. This is what a style asks for once a control is on screen.
    /// </summary>
    [Fact]
    public void ThePathsAreReadableWithoutTheRenderingPlatform()
    {
        Assert.Equal(ProviderIdentity.DiamondPath, ProviderIdentity.GlyphPath("claude"));
        Assert.Equal(ProviderIdentity.HexagonPath, ProviderIdentity.GlyphPath("codex"));
        Assert.Equal(ProviderIdentity.CirclePath, ProviderIdentity.GlyphPath("gemini"));
        Assert.StartsWith("M", AltimIcons.HomePath, StringComparison.Ordinal);
        Assert.StartsWith("M", AltimIcons.SettingsPath, StringComparison.Ordinal);
    }
}
