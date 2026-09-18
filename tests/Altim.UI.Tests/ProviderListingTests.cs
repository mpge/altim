using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// Which providers the usage surfaces list. Altim registers every provider it knows how to
/// read, so a machine with one agent installed had a panel listing three.
/// </summary>
/// <remarks>
/// <para>
/// The rule itself is <c>ProviderVisibility</c>'s and is tested over every status in
/// <c>Altim.Core.Tests</c>. What is tested here is that each surface applies it, and applies
/// it to everything it draws from the same set: a provider dropped from the rows but left
/// with a ring on the dial, a row in the legend or a line under Resets would be worse than
/// the clutter it was removing.
/// </para>
/// <para>
/// Every test here that hides something has a partner that proves the same surface keeps a
/// failed provider, because "hide what has nothing to say" and "hide what has nothing on it"
/// are the same assertion until one of them is written down.
/// </para>
/// </remarks>
public sealed class ProviderListingTests
{
    /// <summary>
    /// The tray panel drops a provider that is not installed from its rows, its rings, its
    /// legend and its resets at once.
    /// </summary>
    [AvaloniaFact]
    public void ThePanelDoesNotListAProviderThatIsNotInstalled()
    {
        using PopupViewModel panel = Panel(
            Reading("claude", "Claude Code", Readings.Healthy("claude")),
            Reading("gemini", "Gemini CLI", Readings.NotDetected("gemini")));

        Assert.Equal(2, panel.Providers.Count);
        Assert.Single(panel.VisibleProviders);
        Assert.Equal("Claude Code", panel.VisibleProviders[0].DisplayName);
        Assert.True(panel.HasVisibleProviders);

        // One ring, one legend row, one reset line: everything the panel builds from the
        // rows is built from the listed rows.
        Assert.Single(panel.DialReadings);
        Assert.Single(panel.Resets);
        Assert.Equal("Claude Code", panel.Resets[0].ProviderName);

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.True(Surface.Shows(window, "Claude Code"));
            Assert.False(Surface.Shows(window, "Gemini CLI"));
            Assert.False(Surface.Shows(window, UsageFormat.MetricUnavailable));
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// The rings, the legend and the reset lines are filtered themselves rather than left to
    /// inherit the filtering by accident.
    /// </summary>
    /// <remarks>
    /// A provider that is not installed reports no figures, so on real readings a ring for
    /// one would not be drawn whether or not anything filtered it. That makes the ordinary
    /// case unable to tell a rule that was applied from a rule that was forgotten, which is
    /// the whole point of this test: the reading here says not detected and carries a
    /// percentage and a reset instant anyway. Nothing prevents a provider handing Altim that
    /// pair, and if one did, the panel would list two rings and name one of them after a
    /// provider with no row.
    /// </remarks>
    [AvaloniaFact]
    public void ThePanelDrawsNoRingAndNoResetForAProviderItDoesNotList()
    {
        var ghost = new ProviderUsage(
            "gemini",
            ProviderStatus.NotDetected,
            [Readings.Metric("five_hour", "Session", 12d, TimeSpan.FromHours(5), Readings.Now.AddHours(2))],
            null,
            Readings.Now,
            null);

        using PopupViewModel panel = Panel(
            Reading("claude", "Claude Code", Readings.Healthy("claude")),
            Reading("gemini", "Gemini CLI", ghost));

        Assert.Single(panel.VisibleProviders);
        Assert.Single(panel.DialReadings);
        Assert.Contains("Claude Code", panel.DialReadings[0].Label, StringComparison.Ordinal);
        Assert.Single(panel.Resets);
        Assert.Equal("Claude Code", panel.Resets[0].ProviderName);

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.False(Surface.Shows(window, "Gemini CLI"));
            Assert.False(Surface.Shows(window, "12%"));
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// <b>The tray panel keeps a provider whose reading failed.</b> It is installed and
    /// Altim could not read it, which is the thing this product exists to say. A rule that
    /// only hid what is absent and a rule that hides anything with no figures look identical
    /// until this is written down.
    /// </summary>
    [AvaloniaFact]
    public void ThePanelStillListsAProviderWhoseReadingFailed()
    {
        using PopupViewModel panel = Panel(
            Reading("claude", "Claude Code", Readings.Healthy("claude")),
            Reading("codex", "Codex", Readings.Failed("codex")));

        Assert.Equal(2, panel.VisibleProviders.Count);

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.True(Surface.Shows(window, "Codex"));
            Assert.True(Surface.Shows(window, UsageFormat.ProviderUnavailable));
            Assert.True(Surface.Shows(window, UsageFormat.RetryLabel));
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// A provider nobody has probed yet is listed. The panel is built before the first
    /// reading, so hiding this state would put every row on screen a moment after the panel
    /// opened and push whatever the reader was pointing at down the panel.
    /// </summary>
    [AvaloniaFact]
    public void ThePanelListsAProviderItHasNotProbedYet()
    {
        var waiting = new FakeUsageProvider("gemini", "Gemini CLI", Readings.Healthy("gemini", 12d))
        {
            Status = ProviderStatus.Unknown,
        };

        using var panel = new PopupViewModel(
            [waiting], new TestClock(Readings.Now), AltimSettings.Default);

        // Nothing has been applied, so the row is in the state it is built in.
        Assert.Equal(ProviderStatus.Unknown, panel.Providers[0].Status.Status);
        Assert.Single(panel.VisibleProviders);

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.True(Surface.Shows(window, "Gemini CLI"));
            Assert.True(Surface.Shows(window, UsageFormat.MetricUnavailable));
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// A machine with none of the providers installed gets the sentence, not a blank panel.
    /// The status line arrives at the same words over the same providers, which is the point
    /// of leaving it unfiltered.
    /// </summary>
    [AvaloniaFact]
    public void APanelWithNothingInstalledSaysSo()
    {
        using PopupViewModel panel = Panel(
            Reading("claude", "Claude Code", Readings.NotDetected("claude")),
            Reading("codex", "Codex", Readings.NotDetected("codex")),
            Reading("gemini", "Gemini CLI", Readings.NotDetected("gemini")));

        Assert.Empty(panel.VisibleProviders);
        Assert.False(panel.HasVisibleProviders);
        Assert.Empty(panel.DialReadings);
        Assert.Empty(panel.Resets);
        Assert.Equal(UsageFormat.NoProviders, panel.StatusLine);

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.True(Surface.Shows(window, UsageFormat.NoProviders));
            Assert.False(Surface.Shows(window, "Claude Code"));
            Assert.False(Surface.Shows(window, "Codex"));
            Assert.False(Surface.Shows(window, "Gemini CLI"));
            Assert.Empty(Surface.Visible<Dial>(window));
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// The status line speaks for every provider, listed or not. It is the one line that
    /// says why the panel is shorter than the machine, and dropping the absent provider from
    /// it would leave nothing anywhere saying Altim knows about it.
    /// </summary>
    [AvaloniaFact]
    public void TheStatusLineStillNamesAProviderTheRowsLeaveOut()
    {
        using PopupViewModel panel = Panel(
            Reading("claude", "Claude Code", Readings.Healthy("claude")),
            Reading("codex", "Codex", Readings.Healthy("codex", 41d)),
            Reading("gemini", "Gemini CLI", Readings.NotDetected("gemini")));

        Assert.Equal(2, panel.VisibleProviders.Count);
        Assert.Equal("Gemini CLI not detected", panel.StatusLine);
    }

    /// <summary>
    /// A provider settles out of the list when its first reading lands, and back into it if
    /// it is ever found: the rows follow the reading rather than the state the panel was
    /// built in.
    /// </summary>
    [AvaloniaFact]
    public void AProviderLeavesAndRejoinsTheListAsItsReadingChanges()
    {
        var gemini = new FakeUsageProvider("gemini", "Gemini CLI", Readings.Healthy("gemini", 12d));
        using var panel = new PopupViewModel(
            [gemini], new TestClock(Readings.Now), AltimSettings.Default);

        panel.Providers[0].Apply(Readings.Healthy("gemini", 12d));
        Assert.Single(panel.VisibleProviders);

        panel.Providers[0].Apply(Readings.NotDetected("gemini"));
        Assert.Empty(panel.VisibleProviders);
        Assert.False(panel.HasVisibleProviders);

        panel.Providers[0].Apply(Readings.Healthy("gemini", 12d));
        Assert.Single(panel.VisibleProviders);
        Assert.True(panel.HasVisibleProviders);
    }

    /// <summary>
    /// A provider that is not installed is still read, which is what lets it ever come back.
    /// Dropping it from the list must not drop it from the work.
    /// </summary>
    [AvaloniaFact]
    public async Task AProviderThatIsNotListedIsStillRead()
    {
        var gemini = new FakeUsageProvider("gemini", "Gemini CLI", Readings.NotDetected("gemini"));
        using var panel = new PopupViewModel(
            [gemini], new TestClock(Readings.Now), AltimSettings.Default);

        await panel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(panel.VisibleProviders);
        Assert.Equal(1, gemini.UsageReads);
    }

    /// <summary>Overview gives a card to the providers this machine has, and no others.</summary>
    [AvaloniaFact]
    public void OverviewCardsOnlyTheProvidersThisMachineHas()
    {
        using OverviewViewModel overview = Page(
            Reading("claude", "Claude Code", Readings.Healthy("claude")),
            Reading("gemini", "Gemini CLI", Readings.NotDetected("gemini")));

        Assert.Equal(2, overview.Providers.Count);
        Assert.Single(overview.VisibleProviders);
        Assert.Equal("Claude Code", overview.VisibleProviders[0].DisplayName);
        Assert.True(overview.HasVisibleProviders);
    }

    /// <summary>
    /// <b>Overview keeps the card of a provider whose reading failed.</b> That card is the
    /// sentence and the Retry, which is the page doing its job rather than taking up room.
    /// </summary>
    [AvaloniaFact]
    public void OverviewKeepsTheCardOfAProviderWhoseReadingFailed()
    {
        using OverviewViewModel overview = Page(
            Reading("claude", "Claude Code", Readings.Healthy("claude")),
            Reading("codex", "Codex", Readings.Failed("codex")));

        Assert.Equal(2, overview.VisibleProviders.Count);
    }

    /// <summary>Overview with nothing installed says so rather than showing an empty grid.</summary>
    [AvaloniaFact]
    public void OverviewWithNothingInstalledSaysSo()
    {
        using OverviewViewModel overview = Page(
            Reading("claude", "Claude Code", Readings.NotDetected("claude")),
            Reading("codex", "Codex", Readings.NotDetected("codex")),
            Reading("gemini", "Gemini CLI", Readings.NotDetected("gemini")));

        Assert.Empty(overview.VisibleProviders);
        Assert.False(overview.HasVisibleProviders);
        Assert.Equal(UsageFormat.NoProviders, overview.StatusLine);

        Surface.Show(new OverviewView { DataContext = overview }, window =>
        {
            Assert.True(Surface.Shows(window, UsageFormat.NoProviders));
            Assert.False(Surface.Shows(window, "Gemini CLI"));
        }, width: 1000d, height: 900d);
    }

    /// <summary>
    /// The sidebar is the exception, deliberately. A provider that is not installed keeps its
    /// navigation row and its page, because navigation is a table of contents rather than a
    /// report: that page is where the sentence saying it is not installed lives, and where
    /// the Retry is if it is failing. Dropping it would leave a reader who has just installed
    /// an agent with nowhere to look until Altim was restarted.
    /// </summary>
    [Fact]
    public void TheSidebarKeepsAPageForEveryProvider()
    {
        IUsageProvider[] providers =
        [
            new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude")),
            new FakeUsageProvider("gemini", "Gemini CLI", Readings.NotDetected("gemini")),
        ];

        using var dashboard = new DashboardViewModel(
            providers,
            new FakeHistoryService(),
            new FakeSettingsStore(),
            new FakeStatusLineService(),
            new TestClock(Readings.Now));

        dashboard.Providers[1].Apply(Readings.NotDetected("gemini"));

        Assert.Collection(
            dashboard.Sections,
            section => Assert.Equal("Overview", section.Title),
            section => Assert.Equal("Claude Code", section.Title),
            section => Assert.Equal("Gemini CLI", section.Title),
            section => Assert.Equal("History", section.Title),
            section => Assert.Equal("Settings", section.Title));

        // And Overview, on the same rows, gives it no card.
        Assert.Single(dashboard.Overview.VisibleProviders);
    }

    private static FakeUsageProvider Reading(string id, string name, ProviderUsage usage) =>
        new(id, name, usage);

    private static PopupViewModel Panel(params FakeUsageProvider[] providers)
    {
        var panel = new PopupViewModel(providers, new TestClock(Readings.Now), AltimSettings.Default);
        for (int i = 0; i < providers.Length; i++)
        {
            panel.Providers[i].Apply(providers[i].Reading);
        }

        return panel;
    }

    private static OverviewViewModel Page(params FakeUsageProvider[] providers)
    {
        List<ProviderViewModel> rows = [];
        foreach (FakeUsageProvider provider in providers)
        {
            rows.Add(new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default));
        }

        var page = new OverviewViewModel(rows, new FakeHistoryService(), new TestClock(Readings.Now));
        for (int i = 0; i < providers.Length; i++)
        {
            rows[i].Apply(providers[i].Reading);
        }

        return page;
    }
}
