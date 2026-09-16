using Altim.Core.Abstractions;
using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
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
