using System.Diagnostics;
using Altim.Core.Abstractions;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The dashboard's contract: a section per provider without a view knowing which providers
/// exist, and a sidebar selection that actually changes what is on screen.
/// </summary>
public sealed class DashboardTests
{
    private static DashboardViewModel Build(
        out FakeHistoryService history,
        out FakeSettingsStore settings)
    {
        history = new FakeHistoryService();
        settings = new FakeSettingsStore();

        IUsageProvider[] providers =
        [
            new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude")),
            new FakeUsageProvider("codex", "Codex", Readings.Healthy("codex", 41d)),
        ];

        return new DashboardViewModel(providers, history, settings, new TestClock(Readings.Now));
    }

    /// <summary>The sections are Overview, one per provider, then History and Settings.</summary>
    [Fact]
    public void BuildsASectionPerProvider()
    {
        using DashboardViewModel dashboard = Build(out _, out _);

        Assert.Collection(
            dashboard.Sections,
            section => Assert.Equal("Overview", section.Title),
            section => Assert.Equal("Claude Code", section.Title),
            section => Assert.Equal("Codex", section.Title),
            section => Assert.Equal("History", section.Title),
            section => Assert.Equal("Settings", section.Title));

        // Every row carries a mark now. What differs is which kind: the pages take a line
        // icon and a provider takes its own glyph in its own accent.
        Assert.Equal(NavigationIcon.Overview, dashboard.Sections[0].Icon);
        Assert.False(dashboard.Sections[0].IsProviderMark);
        Assert.True(dashboard.Sections[1].IsProviderMark);
        Assert.True(dashboard.Sections[1].IsAnthropic);
        Assert.True(dashboard.Sections[2].IsOpenAI);
        Assert.Equal(NavigationIcon.History, dashboard.Sections[3].Icon);
        Assert.Equal(NavigationIcon.Settings, dashboard.Sections[4].Icon);
    }

    /// <summary>Opening the window reads nothing until something asks it to.</summary>
    [Fact]
    public void ConstructionReadsNothing()
    {
        using DashboardViewModel dashboard = Build(out _, out FakeSettingsStore settings);

        Assert.Equal(0, settings.Writes);
        Assert.IsType<OverviewViewModel>(dashboard.CurrentPage);
        Assert.Empty(dashboard.Providers[0].Metrics);
    }

    /// <summary>Selecting a section changes the page the window shows.</summary>
    [Fact]
    public void SelectionChangesThePage()
    {
        using DashboardViewModel dashboard = Build(out _, out _);

        dashboard.SelectedSection = dashboard.Sections[1];
        ProviderPageViewModel provider = Assert.IsType<ProviderPageViewModel>(dashboard.CurrentPage);
        Assert.Equal("Claude Code", provider.Title);

        dashboard.SelectedSection = dashboard.Sections[3];
        Assert.IsType<HistoryViewModel>(dashboard.CurrentPage);

        dashboard.SelectedSection = dashboard.Sections[4];
        Assert.IsType<SettingsViewModel>(dashboard.CurrentPage);
    }

    /// <summary>Overview and the provider pages report on the same rows, not on copies.</summary>
    [Fact]
    public void PagesShareTheProviderRows()
    {
        using DashboardViewModel dashboard = Build(out _, out _);

        dashboard.SelectedSection = dashboard.Sections[1];
        ProviderPageViewModel page = Assert.IsType<ProviderPageViewModel>(dashboard.CurrentPage);

        Assert.Same(dashboard.Providers[0], page.Provider);
        Assert.Same(dashboard.Providers[0], dashboard.Overview.Providers[0]);
        Assert.Same(dashboard.Providers[0], dashboard.Settings.Providers[0]);
    }

    /// <summary>The greeting on Overview comes from the clock the dashboard was given.</summary>
    [Fact]
    public async Task OverviewGreetsFromTheClock()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 15, 19, 0, 0, TimeSpan.Zero));
        var history = new FakeHistoryService();
        var settings = new FakeSettingsStore();
        IUsageProvider[] providers = [new FakeUsageProvider("claude", "Claude Code")];

        using var dashboard = new DashboardViewModel(providers, history, settings, clock);
        await dashboard.LoadAsync(TestContext.Current.CancellationToken);

        // The greeting is the eyebrow above the title now, and the title names the page.
        Assert.Equal("Good evening", dashboard.Overview.GreetingText);
        Assert.Equal("GOOD EVENING", dashboard.Overview.EyebrowText);
        Assert.Equal("Usage overview", dashboard.Overview.PageTitle);
        Assert.Equal("Here's how your AI agents are doing.", dashboard.Overview.SubHeading);
    }

    /// <summary>
    /// A provider that blocks its caller costs the window nothing. The fake blocks the thread
    /// before its first await, which is what opening a database or running a CLI actually does,
    /// so a window that read on the dispatcher thread would open frozen.
    /// </summary>
    [Fact]
    public void ASlowProviderBlocksNeitherOpeningNorNavigating()
    {
        using var gate = new ManualResetEventSlim(false);
        var quick = new FakeUsageProvider("claude", "Claude Code");
        var slow = new FakeUsageProvider("codex", "Codex", Readings.Healthy("codex", 41d)) { Block = gate };
        IUsageProvider[] providers = [quick, slow];

        var stopwatch = Stopwatch.StartNew();
        using var dashboard = new DashboardViewModel(
            providers,
            new FakeHistoryService(),
            new FakeSettingsStore(),
            new TestClock(Readings.Now));

        // Navigating to the blocked provider's page returns at once. The page loads itself
        // rather than the caller awaiting it, which is the whole point of IDashboardPage.
        dashboard.SelectedSection = dashboard.Sections[2];
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Opening and navigating took {stopwatch.Elapsed}.");

        // Nothing is shown while the reading is outstanding: a zero here would be a number no
        // provider has reported. Overview was never selected, so the quick one was never read.
        Assert.Empty(dashboard.Providers[1].Metrics);
        Assert.Equal(0, quick.UsageReads);

        // Released so the outstanding read finishes rather than parking a pool thread. What
        // the reading then carries is ProviderViewModelTests' business, not the window's.
        gate.Set();
    }

    /// <summary>On screen, the sidebar selection swaps the view in the content area.</summary>
    [AvaloniaFact]
    public void SidebarSelectionSwapsTheView()
    {
        using DashboardViewModel dashboard = Build(out _, out _);
        var window = new DashboardWindow(dashboard) { Width = 1000d, Height = 720d };

        Surface.Render(window, _ =>
        {
            Assert.Single(Surface.Visible<OverviewView>(window));
            Assert.Empty(Surface.Visible<HistoryView>(window));

            ListBox sidebar = Assert.Single(Surface.Visible<ListBox>(window));
            Assert.Equal(5, sidebar.ItemCount);

            sidebar.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();

            Assert.Same(dashboard.Sections[3], dashboard.SelectedSection);
            Assert.Single(Surface.Visible<HistoryView>(window));
            Assert.Empty(Surface.Visible<OverviewView>(window));

            sidebar.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            Assert.Single(Surface.Visible<ProviderPageView>(window));
            Assert.True(Surface.Shows(window, "Claude Code"));
        });
    }
}
