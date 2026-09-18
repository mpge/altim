using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
    /// <b>A provider the panel lists keeps its ring, even with nothing to put on it.</b>
    /// </summary>
    /// <remarks>
    /// The ring used to be taken from the provider's headline, and a provider with no metrics
    /// has no headline, so a machine with three providers and one unreachable drew two rings
    /// and named two in the legend while three rows stood underneath. DESIGN.md and the view's
    /// own comment both say the opposite in words: "a ring missing from the legend would be a
    /// provider missing from the panel". The rule held for a window with no percentage, which
    /// is the case a user rarely meets, and not for the failed reading, which is the case they
    /// do. The ring has no window to be named after, so it takes the provider's own name and
    /// the em dash every unreported reading takes.
    /// </remarks>
    [AvaloniaFact]
    public void AProviderThatCouldNotBeReadKeepsItsRingAndItsLegendRow()
    {
        using PopupViewModel panel = Panel(
            Claude(),
            Codex(),
            new FakeUsageProvider("gemini", "Gemini CLI", Readings.Failed("gemini")));

        Assert.Equal(3, panel.VisibleProviders.Count);
        Assert.Equal(3, panel.DialReadings.Count);

        Assert.Equal(
            ["Session (Claude Code)", "Session (Codex)", "Gemini CLI"],
            panel.DialReadings.Select(r => r.Label));

        // Never a zero for the one that could not be read, and never a threshold index over
        // an outlined ring either.
        Assert.Null(panel.DialReadings[2].Value);
        Assert.Null(panel.DialReadings[2].Threshold);
        Assert.False(panel.DialReadings[2].IsReported);
        Assert.Equal(UsageFormat.Unknown, panel.DialReadings[2].PercentText);

        // The ring numbers still come from the list, so the legend's marks and the face's
        // arcs cannot be numbered differently.
        Assert.Equal([0, 1, 2], panel.DialReadings.Select(r => r.Ring));
        Assert.Equal([14d, 10d, 6d], panel.DialReadings.Select(r => r.LegendMarkDiameter));

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Dial dial = Assert.Single(Surface.Visible<Dial>(window));
            Assert.Equal(3, dial.Arcs.Count);

            ItemsControl legend = Assert.Single(
                Surface.Visible<ItemsControl>(window),
                items => items.ItemsSource is IReadOnlyList<DialReading>);

            Assert.Equal(
                ["Session (Claude Code)", "62%", "Session (Codex)", "41%", "Gemini CLI", UsageFormat.Unknown],
                Surface.Lines(legend));

            // The provider is under the face as well, with the failure and the one action
            // that can change it, which is what makes a ring with no figure readable at all.
            Assert.True(Surface.Shows(window, UsageFormat.ProviderUnavailable));
            Assert.DoesNotContain("0%", Surface.Lines(window));
        }, width: 320d, height: 1400d);
    }

    /// <summary>
    /// The one line stays inside the panel when a provider reports five windows.
    /// </summary>
    /// <remarks>
    /// It was a horizontal <c>StackPanel</c> with no wrapping, trimming or maximum width, so
    /// on a provider reporting five windows the line simply kept going, out of the 320 wide
    /// panel and into the shadow margin. It wraps rather than trims: a summary with the last
    /// two windows cut off reads as the whole list, and this product does not hide figures it
    /// has. Claude Code reports three seven-day windows, so the line is also where two windows
    /// of one length had to stop being called the same thing.
    /// </remarks>
    [AvaloniaFact]
    public void TheCompactLineStaysInsideThePanel()
    {
        var crowded = new ProviderUsage(
            "claude",
            ProviderStatus.Active,
            [
                Readings.Metric("five_hour", "Session", 26d, TimeSpan.FromHours(5), Readings.Now.AddHours(2)),
                Readings.Metric("seven_day", "Weekly", 41d, TimeSpan.FromDays(7), Readings.Now.AddDays(3)),
                Readings.Metric("seven_day_opus", "Weekly (Opus)", 63d, TimeSpan.FromDays(7), Readings.Now.AddDays(3)),
                Readings.Metric("seven_day_sonnet", "Weekly (Sonnet)", 22d, TimeSpan.FromDays(7), Readings.Now.AddDays(3)),
                Readings.Metric("thirty_day", "Monthly", 58d, TimeSpan.FromDays(30), Readings.Now.AddDays(11)),
            ],
            null,
            Readings.Now,
            null);

        using PopupViewModel panel = Panel(new FakeUsageProvider("claude", "Claude Code", crowded));

        // Three seven-day windows, three different figures, and three different names. Keyed
        // on the window's length alone all three printed "7d".
        Assert.Equal(
            ["5h", "7d", "7d Opus", "7d Sonnet", "30d"],
            panel.Providers[0].CompactMetrics.Select(m => m.Label));

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            foreach (TextBlock block in Surface.Visible<TextBlock>(window))
            {
                if (block.TranslatePoint(new Point(block.Bounds.Width, 0d), window) is not { } edge)
                {
                    continue;
                }

                Assert.True(
                    edge.X <= 320d,
                    $"\"{block.Text}\" runs to {edge.X:0.#}, past the 320 the panel is wide.");
            }
        }, width: 320d, height: 1400d);
    }

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
    /// The dial is on screen carrying one sweep per provider, and a screen reader is handed
    /// every one of them: whose it is, the level and the threshold. The dial is drawn and has
    /// nothing else to announce, and a client handed only the figure in the middle would be
    /// handed a face with fewer sweeps on it than it has.
    /// </summary>
    [AvaloniaFact]
    public void TheDialIsOnScreenAndNamed()
    {
        using PopupViewModel panel = Panel(Claude(), Codex());

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Dial dial = Assert.Single(Surface.Visible<Dial>(window));

            Assert.Equal(2, dial.Arcs.Count);
            Assert.Equal(62d, dial.Arcs[0].Value);
            Assert.Equal(41d, dial.Arcs[1].Value);
            Assert.Equal(
                AltimSettings.Default.SessionThresholdPercent,
                dial.Arcs[0].Threshold);
            Assert.True(Surface.Shows(window, "Session (Claude Code)"));
            Assert.True(Surface.Shows(window, "Resets in 2h 14m"));

            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(dial);
            Assert.Equal(
                "Usage by provider, Session (Claude Code), 62% used, threshold 80%."
                    + " Session (Codex), 41% used, threshold 80%",
                peer.GetName());

            // Still no meters: the panel's provider lines are one line each, not a stack of
            // rails, and the dial is the one instrument on the surface.
            Assert.Empty(Surface.Visible<Meter>(window));
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// One sweep per provider, on its own ring, in the order the providers were registered,
    /// and each carrying that provider's own head window rather than a figure made out of
    /// several.
    /// </summary>
    /// <remarks>
    /// Registration order is what keeps the picture still. These three are registered lowest
    /// first, so a face that sorted its rings by level would produce a different order here
    /// and would reshuffle itself every time two numbers crossed. Gemini's session reads 12
    /// and its week 38, so its ring is the week: each ring is its own provider's head window,
    /// and two rings can easily be measuring different windows.
    /// </remarks>
    [AvaloniaFact]
    public void TheDialCarriesOneSweepPerProviderInRegistrationOrder()
    {
        using PopupViewModel panel = Panel(Gemini(), Codex(), Claude());

        Assert.Equal(3, panel.DialReadings.Count);
        Assert.Equal(
            ["Weekly (Gemini CLI)", "Session (Codex)", "Session (Claude Code)"],
            panel.DialReadings.Select(r => r.Label));
        Assert.Equal([38d, 41d, 62d], panel.DialReadings.Select(r => r.Value));

        // The ring numbers are the list positions, which is what the legend sizes its marks
        // from: a reading numbered differently from where it is drawn would put the legend's
        // circles in one order and the arcs in another.
        Assert.Equal([0, 1, 2], panel.DialReadings.Select(r => r.Ring));
        Assert.Equal([14d, 10d, 6d], panel.DialReadings.Select(r => r.LegendMarkDiameter));

        // The figure in the middle is always one of the rings, never a fourth number.
        Assert.Equal("Session (Claude Code)", panel.Headline?.SourceLabel);
        Assert.Contains(panel.Headline?.SourceLabel, panel.DialReadings.Select(r => r.Label));

        // Each ring is its own provider's head window, so a provider's weekly figure never
        // ends up on another provider's ring.
        for (int i = 0; i < panel.Providers.Count; i++)
        {
            Assert.Equal(panel.Providers[i].Headline?.SourceLabel, panel.DialReadings[i].Label);
            Assert.Equal(panel.Providers[i].Headline?.Value, panel.DialReadings[i].Value);
        }
    }

    /// <summary>
    /// <b>No figure on the panel is two providers' figures put together.</b> They are
    /// proportions of two different, undisclosed allowances: their sum, their mean and their
    /// difference all measure nothing, and a number that looks authoritative and measures
    /// nothing is the one thing this product will not show.
    /// </summary>
    [AvaloniaFact]
    public void ThePanelNeverShowsACombinedFigure()
    {
        using PopupViewModel panel = Panel(Claude(), Codex());

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            IReadOnlyList<string> lines = Surface.Lines(window);

            Assert.Contains("62%", lines);
            Assert.Contains("41%", lines);

            // 62 + 41, their mean either way rounded, and their difference.
            foreach (string invented in (string[])["103%", "52%", "51%", "21%"])
            {
                Assert.DoesNotContain(invented, lines);
            }
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// The legend names every ring, in the rings' own order, with each provider's figure
    /// beside it and a mark the size of the ring it stands for.
    /// </summary>
    /// <remarks>
    /// A ring with nothing naming it is a ring nobody can read, and this is the panel's
    /// primary surface. The window on the dial is named twice on purpose: once under the face
    /// as the figure standing in it, and once in the legend as one arc of several. They are
    /// two questions - "what is the big number" and "which arc is whose" - and answering only
    /// the first leaves the other arcs anonymous.
    /// </remarks>
    [AvaloniaFact]
    public void TheLegendNamesEveryRing()
    {
        using PopupViewModel panel = Panel(Claude(), Codex());

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            ItemsControl legend = Assert.Single(
                Surface.Visible<ItemsControl>(window),
                items => items.ItemsSource is IReadOnlyList<DialReading>);

            Assert.Equal(
                ["Session (Claude Code)", "62%", "Session (Codex)", "41%"],
                Surface.Lines(legend));

            // The marks are circles, shrinking inward the way the rings do.
            IReadOnlyList<Ellipse> marks = Surface.Visible<Ellipse>(legend);
            Assert.Equal(2, marks.Count);
            Assert.Equal(14d, marks[0].Bounds.Width, 6);
            Assert.Equal(10d, marks[1].Bounds.Width, 6);
            Assert.True(
                marks[0].Bounds.Width > marks[1].Bounds.Width,
                "The legend's marks do not shrink the way the rings do.");

            // The window on the dial is under the face as well, which is the one repetition
            // the section makes and makes on purpose.
            Assert.Equal(2, Surface.Count(window, "Session (Claude Code)"));
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// A provider that reports no figure keeps its ring and its legend row, and is named as
    /// unreported rather than drawn as a nothing. A ring missing from the legend would be a
    /// provider missing from the panel.
    /// </summary>
    [AvaloniaFact]
    public void AProviderWithNoFigureKeepsItsRingAndSaysSo()
    {
        var silent = new FakeUsageProvider("codex", "Codex", new ProviderUsage(
            "codex",
            ProviderStatus.Idle,
            [Readings.Metric("five_hour", "Session", null, TimeSpan.FromHours(5), Readings.Now.AddHours(1))],
            null,
            Readings.Now,
            null));

        using PopupViewModel panel = Panel(Claude(), silent);

        Assert.Equal(2, panel.DialReadings.Count);
        Assert.Null(panel.DialReadings[1].Value);
        Assert.False(panel.DialReadings[1].IsReported);
        Assert.Equal(UsageFormat.Unknown, panel.DialReadings[1].PercentText);

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Dial dial = Assert.Single(Surface.Visible<Dial>(window));

            // The face still has a reading on it, because the other provider reported one.
            Assert.False(dial.IsUnavailable);
            Assert.Equal(2, dial.Arcs.Count);

            ItemsControl legend = Assert.Single(
                Surface.Visible<ItemsControl>(window),
                items => items.ItemsSource is IReadOnlyList<DialReading>);

            Assert.Equal(
                ["Session (Claude Code)", "62%", "Session (Codex)", UsageFormat.Unknown],
                Surface.Lines(legend));

            // And never a zero for the provider that reported nothing.
            Assert.DoesNotContain("0%", Surface.Lines(legend));
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

        Assert.False(panel.HasVisibleProviders);

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            Assert.True(Surface.Shows(window, UsageFormat.NoProviders));
            Assert.Empty(Surface.Visible<Meter>(window));
        }, width: 320d);
    }
}
