using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The panel has to be right in four shapes: one provider, three providers, a provider that
/// reported nothing, and a provider that could not be read at all.
/// </summary>
public sealed class PopupTests
{
    private static PopupViewModel Panel(params FakeUsageProvider[] providers)
    {
        var panel = new PopupViewModel(providers, new TestClock(Readings.Now), AltimSettings.Default);

        // Applied rather than loaded: the reading is the fake's, and this keeps the render
        // tests free of a scheduler and a thread pool hop.
        for (int i = 0; i < providers.Length; i++)
        {
            panel.Providers[i].Apply(providers[i].Reading);
        }

        return panel;
    }

    private static FakeUsageProvider Claude() =>
        new("claude", "Claude Code", Readings.Healthy("claude"));

    private static FakeUsageProvider Codex() =>
        new("codex", "Codex", Readings.Healthy("codex", 41d));

    private static FakeUsageProvider Gemini() =>
        new("gemini", "Gemini CLI", Readings.Healthy("gemini", 12d));

    /// <summary>
    /// One provider: the header, a name, one compact line carrying every window it
    /// reports, the reset it is next due, and the action. No meters: the panel names the
    /// windows by their length and puts the figures beside them on one line.
    /// </summary>
    [AvaloniaFact]
    public void RendersOneProvider()
    {
        using PopupViewModel panel = Panel(Claude());

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.True(Surface.Shows(window, "Altim"));
            Assert.True(Surface.Shows(window, "Claude Code"));
            Assert.True(Surface.Shows(window, "62%"));
            Assert.True(Surface.Shows(window, "38%"));
            Assert.True(Surface.Shows(window, "5h"));
            Assert.True(Surface.Shows(window, "7d"));
            Assert.True(Surface.Shows(window, "Resets in"));
            Assert.True(Surface.Shows(window, "Open Altim"));
            Assert.True(Surface.Shows(window, "Session (Claude Code)"));
            Assert.Empty(Surface.Visible<Meter>(window));
            Assert.False(Surface.Shows(window, UsageFormat.ProviderUnavailable));
        }, width: 320d);
    }

    /// <summary>Three providers: three rows, and every row's own metrics.</summary>
    [AvaloniaFact]
    public void RendersThreeProviders()
    {
        using PopupViewModel panel = Panel(Claude(), Codex(), Gemini());

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.True(Surface.Shows(window, "Claude Code"));
            Assert.True(Surface.Shows(window, "Codex"));
            Assert.True(Surface.Shows(window, "Gemini CLI"));
            Assert.True(Surface.Shows(window, "62%"));
            Assert.True(Surface.Shows(window, "41%"));
            Assert.True(Surface.Shows(window, "12%"));
            Assert.Empty(Surface.Visible<Meter>(window));

            // One reset line per provider, not one per window.
            Assert.Equal(3, panel.Resets.Count);
        }, width: 320d, height: 1400d);
    }

    /// <summary>A provider that reported no metric says so, and draws no meter at all.</summary>
    [AvaloniaFact]
    public void RendersAProviderWithNoMetrics()
    {
        using PopupViewModel panel = Panel(new FakeUsageProvider("codex", "Codex", Readings.NoMetrics("codex")));

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.True(Surface.Shows(window, "Codex"));
            Assert.True(Surface.Shows(window, UsageFormat.MetricUnavailable));
            Assert.Empty(Surface.Visible<Meter>(window));
            Assert.DoesNotContain("0%", Surface.Lines(window));
        }, width: 320d);
    }

    /// <summary>A provider that could not be read shows the sentence and an enabled retry.</summary>
    [AvaloniaFact]
    public void RendersAnErroredProvider()
    {
        using PopupViewModel panel = Panel(new FakeUsageProvider("claude", "Claude Code", Readings.Failed("claude")));

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            // The sentence and the retry carry the failure. The row has no status word of
            // its own any more: the panel says once, at the foot, where the integrations
            // stand as a whole, and saying it twice on a 320px surface is saying it twice.
            Assert.True(Surface.Shows(window, UsageFormat.ProviderUnavailable));
            Assert.Equal("Claude Code unavailable", panel.StatusLine);
            Assert.True(panel.StatusIsError);
            Assert.Empty(Surface.Visible<Meter>(window));
            Assert.DoesNotContain("0%", Surface.Lines(window));

            Button retry = Surface.Visible<Button>(window)
                .Single(button => string.Equals(button.Content as string, UsageFormat.RetryLabel, StringComparison.Ordinal));
            Assert.True(retry.IsEffectivelyEnabled);
        }, width: 320d);
    }

    /// <summary>The status line names providers by name, never by identifier.</summary>
    [Fact]
    public void StatusLineNamesTheProvider()
    {
        using PopupViewModel panel = Panel(new FakeUsageProvider("claude", "Claude Code", Readings.Failed("claude")));

        Assert.Equal("Claude Code unavailable", panel.StatusLine);
    }

    /// <summary>Every reported reset instant becomes a row, soonest first.</summary>
    [Fact]
    public void ListsResetsSoonestFirst()
    {
        using PopupViewModel panel = Panel(Claude(), Codex());

        // One line per provider, carrying the soonest window that provider reports.
        Assert.True(panel.HasResets);
        Assert.Equal(2, panel.Resets.Count);
        Assert.Equal("2h 14m", panel.Resets[0].RemainingText);
        Assert.Equal("Claude Code", panel.Resets[0].ProviderName);
        Assert.Equal("(Claude Code)", panel.Resets[0].ProviderLabel);
        Assert.Equal("Codex", panel.Resets[1].ProviderName);
    }

    /// <summary>A panel whose providers report no reset instant says so rather than guessing.</summary>
    [AvaloniaFact]
    public void SaysWhenNoResetIsReported()
    {
        using PopupViewModel panel = Panel(new FakeUsageProvider("codex", "Codex", Readings.NoMetrics("codex")));

        Assert.False(panel.HasResets);

        Surface.Show(new PopupView { DataContext = panel }, window =>
            Assert.True(Surface.Shows(window, UsageFormat.NoResetsReported)), width: 320d);
    }

    /// <summary>The panel's one action asks for the dashboard rather than opening it itself.</summary>
    [Fact]
    public void ActionAsksForTheDashboard()
    {
        using PopupViewModel panel = Panel(Claude());
        int asked = 0;
        panel.OpenRequested += (_, _) => asked++;

        panel.OpenAltimCommand.Execute(null);

        Assert.Equal(1, asked);
        Assert.Equal("Open Altim", panel.OpenLabel);
    }

    /// <summary>
    /// The panel leads with the window nearest its ceiling, whichever provider reports it,
    /// and names that window and its provider so the figure is never ambiguous.
    /// </summary>
    [Fact]
    public void TheDialShowsTheWindowNearestItsCeiling()
    {
        // Codex reports the higher weekly figure and Claude Code the higher session one.
        using PopupViewModel panel = Panel(
            new FakeUsageProvider("codex", "Codex", Readings.Healthy("codex", 41d)),
            Claude());

        HeadlineReadingViewModel headline = Assert.IsType<HeadlineReadingViewModel>(panel.Headline);

        Assert.True(panel.HasHeadline);
        Assert.Equal(62d, headline.Value);
        Assert.Equal("62%", headline.PercentText);
        Assert.Equal("Session (Claude Code)", headline.SourceLabel);
        Assert.Equal("Resets in 2h 14m", headline.ResetText);
        Assert.Equal(AltimSettings.Default.SessionThresholdPercent, headline.Threshold);
        Assert.False(headline.IsAboveThreshold);
    }

    /// <summary>
    /// Level for level, the window that rolls over first is the one on the dial: it is the
    /// one reached first. Without the tie break the answer would be whichever provider
    /// happened to be registered first.
    /// </summary>
    [Fact]
    public void ALevelTieGoesToTheWindowThatRollsOverFirst()
    {
        var late = new FakeUsageProvider("codex", "Codex", new ProviderUsage(
            "codex",
            ProviderStatus.Active,
            [Readings.Metric("five_hour", "Session", 62d, TimeSpan.FromHours(5), Readings.Now.AddHours(4))],
            null,
            Readings.Now,
            null));

        var soon = new FakeUsageProvider("claude", "Claude Code", new ProviderUsage(
            "claude",
            ProviderStatus.Active,
            [Readings.Metric("five_hour", "Session", 62d, TimeSpan.FromHours(5), Readings.Now.AddMinutes(20))],
            null,
            Readings.Now,
            null));

        using PopupViewModel first = Panel(late, soon);
        Assert.Equal("Session (Claude Code)", first.Headline?.SourceLabel);

        // And the other way round, so the answer is the reset time rather than the order.
        using PopupViewModel second = Panel(soon, late);
        Assert.Equal("Session (Claude Code)", second.Headline?.SourceLabel);
    }

    /// <summary>
    /// A window with no percentage never takes the dial from one that has a figure, and when
    /// nothing anywhere reports one the dial still names a window and shows an em dash rather
    /// than a zero.
    /// </summary>
    [AvaloniaFact]
    public void AWindowWithNoFigureIsNeverPreferredAndNeverReadsAsZero()
    {
        using PopupViewModel reported = Panel(
            new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude", null)));

        // Session reports nothing, so the weekly window carries the dial.
        Assert.Equal("Weekly (Claude Code)", reported.Headline?.SourceLabel);
        Assert.Equal(38d, reported.Headline?.Value);

        using PopupViewModel silent = Panel(new FakeUsageProvider("codex", "Codex", new ProviderUsage(
            "codex",
            ProviderStatus.Idle,
            [Readings.Metric("five_hour", "Session", null, TimeSpan.FromHours(5), Readings.Now.AddHours(1))],
            null,
            Readings.Now,
            null)));

        HeadlineReadingViewModel headline = Assert.IsType<HeadlineReadingViewModel>(silent.Headline);
        Assert.Null(headline.Value);
        Assert.False(headline.IsReported);
        Assert.Equal(UsageFormat.Unknown, headline.PercentText);
        Assert.Equal("Session (Codex)", headline.SourceLabel);

        Surface.Show(new PopupView { DataContext = silent }, window =>
        {
            Dial dial = Assert.Single(Surface.Visible<Dial>(window));
            Assert.Null(dial.Value);
            Assert.True(dial.IsUnavailable);
            Assert.DoesNotContain("0%", Surface.Lines(window));
        }, width: 320d);
    }

    /// <summary>
    /// A window whose provider reports no reset instant says so with an em dash rather than
    /// with a blank line or a time nobody reported.
    /// </summary>
    [Fact]
    public void TheDialSaysWhenNoResetIsReported()
    {
        using PopupViewModel panel = Panel(new FakeUsageProvider("codex", "Codex", new ProviderUsage(
            "codex",
            ProviderStatus.Active,
            [Readings.Metric("five_hour", "Session", 62d, TimeSpan.FromHours(5))],
            null,
            Readings.Now,
            null)));

        HeadlineReadingViewModel headline = Assert.IsType<HeadlineReadingViewModel>(panel.Headline);

        Assert.Equal("Session (Codex)", headline.SourceLabel);
        Assert.Equal("Resets in " + UsageFormat.Unknown, headline.ResetText);
        Assert.False(panel.HasResets);
    }

    /// <summary>
    /// The dial is on screen, carrying the reading and named with the window it belongs to.
    /// A screen reader is handed the provider, the window, the level and the threshold in one
    /// sentence, because the dial is drawn and has nothing else to announce.
    /// </summary>
    [AvaloniaFact]
    public void TheDialIsOnScreenAndNamed()
    {
        using PopupViewModel panel = Panel(Claude(), Codex());

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Dial dial = Assert.Single(Surface.Visible<Dial>(window));

            Assert.Equal(62d, dial.Value);
            Assert.Equal(AltimSettings.Default.SessionThresholdPercent, dial.Threshold);
            Assert.True(Surface.Shows(window, "Session (Claude Code)"));
            Assert.True(Surface.Shows(window, "Resets in 2h 14m"));

            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(dial);
            Assert.Equal("Session (Claude Code), 62% used, threshold 80%", peer.GetName());

            // Still no meters: the panel's provider lines are one line each, not a stack of
            // rails, and the dial is the one instrument on the surface.
            Assert.Empty(Surface.Visible<Meter>(window));
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// A panel with nothing to report at all has no dial and no rule where one would be. A
    /// nameless dial would be furniture: there is no window for it to be showing.
    /// </summary>
    [AvaloniaFact]
    public void ThereIsNoDialWhenThereIsNoWindowToShow()
    {
        using PopupViewModel failed = Panel(
            new FakeUsageProvider("claude", "Claude Code", Readings.Failed("claude")));

        Assert.False(failed.HasHeadline);
        Assert.Null(failed.Headline);

        Surface.Show(new PopupView { DataContext = failed }, window =>
            Assert.Empty(Surface.Visible<Dial>(window)), width: 320d);
    }

    /// <summary>
    /// A reading that fails takes the dial with it. The figure that was on the dial a minute
    /// ago is not a reading of anything now, and a hero carrying a number no provider is
    /// reporting is the one thing this product will not do.
    /// </summary>
    [AvaloniaFact]
    public void AFailedReadingClearsTheDialRatherThanLeavingTheLastFigureOnIt()
    {
        FakeUsageProvider claude = Claude();
        using PopupViewModel panel = Panel(claude);

        Assert.Equal("62%", panel.Headline?.PercentText);

        panel.Providers[0].Apply(Readings.Failed("claude"));

        Assert.Null(panel.Headline);
        Assert.False(panel.HasHeadline);

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.Empty(Surface.Visible<Dial>(window));
            Assert.DoesNotContain("62%", Surface.Lines(window));
        }, width: 320d);
    }

    /// <summary>A panel with no provider at all says so.</summary>
    [AvaloniaFact]
    public void RendersWithNoProviders()
    {
        using var panel = new PopupViewModel(
            Array.Empty<IUsageProvider>(),
            new TestClock(Readings.Now),
            AltimSettings.Default);

        Assert.False(panel.HasProviders);

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.True(Surface.Shows(window, UsageFormat.NoProviders));
            Assert.Empty(Surface.Visible<Meter>(window));
        }, width: 320d);
    }
}
