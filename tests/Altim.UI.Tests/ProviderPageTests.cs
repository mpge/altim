using Altim.Core.Models;
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
/// There is one provider view and one instance of it per provider, so the page has to render
/// whatever a provider reports without knowing which provider it is: the metrics it has, the
/// tokens it has, the sessions it has, and a plain sentence wherever it has none of them.
/// </summary>
public sealed class ProviderPageTests
{
    private const string ProviderId = "claude";
    private const string ProviderName = "Claude Code";

    private static ProviderViewModel Row(FakeUsageProvider provider) =>
        new(provider, new TestClock(Readings.Now), AltimSettings.Default);

    private static AgentSession Working() => Readings.Session(
        ProviderId,
        "session-a",
        "claude-opus-5",
        Readings.Now.AddMinutes(-40),
        Readings.Now.AddMinutes(-2),
        isActive: true,
        new TokenTotals(18_400, 3_200, null, null));

    private static AgentSession Resting() => Readings.Session(
        ProviderId,
        "session-b",
        "claude-sonnet-5",
        Readings.Now.AddHours(-3),
        Readings.Now.AddHours(-2),
        isActive: false);

    /// <summary>The page is named after the provider, which is what the sidebar row reads.</summary>
    [Fact]
    public void TakesItsTitleFromTheProvider()
    {
        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, ProviderName));
        var page = new ProviderPageViewModel(row);

        Assert.Equal(ProviderName, page.Title);
        Assert.Same(row, page.Provider);
        Assert.Equal("Usage", page.UsageHeading);
        Assert.Equal("Tokens", page.TokensHeading);
        Assert.Equal("Activity", page.ActivityHeading);
        Assert.Equal("Integration", page.IntegrationHeading);
    }

    /// <summary>Constructing the page reads nothing at all.</summary>
    [Fact]
    public void ConstructionReadsNothing()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName);
        using ProviderViewModel row = Row(provider);
        var page = new ProviderPageViewModel(row);

        Assert.Equal(0, provider.UsageReads);
        Assert.Empty(page.Provider.Metrics);
        Assert.Empty(page.Provider.Sessions);
    }

    /// <summary>Showing the page takes both readings it needs: usage and activity.</summary>
    [Fact]
    public async Task LoadTakesUsageAndActivity()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName) { Sessions = [Working(), Resting()] };
        using ProviderViewModel row = Row(provider);
        var page = new ProviderPageViewModel(row);

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.UsageReads);
        Assert.True(page.Provider.HasMetrics);
        Assert.True(page.Provider.HasSessions);
        Assert.Equal(2, page.Provider.Sessions.Count);

        // Most recently active first, so the session being worked on is the one at the top.
        Assert.Equal("claude-opus-5", page.Provider.Sessions[0].Title);
        Assert.Equal("Active", page.Provider.Sessions[0].StatusText);
        Assert.Equal("Idle", page.Provider.Sessions[1].StatusText);
    }

    /// <summary>
    /// Activity is a secondary reading. Losing it says nothing about usage, so the usage the
    /// page already has stays on screen and only the activity list empties.
    /// </summary>
    [Fact]
    public async Task ActivityThatCannotBeReadDoesNotLoseTheUsage()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName) { Sessions = [Working()] };
        using ProviderViewModel row = Row(provider);
        var page = new ProviderPageViewModel(row);

        await page.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(page.Provider.HasSessions);

        provider.Sessions = [];
        await page.Provider.LoadSessionsAsync(TestContext.Current.CancellationToken);

        Assert.False(page.Provider.HasSessions);
        Assert.True(page.Provider.HasMetrics);
    }

    /// <summary>On screen: the metrics, the tokens, the sessions and the integration sentence.</summary>
    [AvaloniaFact]
    public async Task RendersEverythingTheProviderReports()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName) { Sessions = [Working(), Resting()] };
        using ProviderViewModel row = Row(provider);
        var page = new ProviderPageViewModel(row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        var view = new ProviderPageView { DataContext = page };

        Surface.Show(view, window =>
        {
            Assert.True(Surface.Shows(window, ProviderName));
            Assert.True(Surface.Shows(window, "Active"));

            Assert.True(Surface.Shows(window, "Usage"));
            Assert.True(Surface.Shows(window, "62%"));
            Assert.True(Surface.Shows(window, "38%"));
            Assert.True(Surface.Shows(window, "Session"));
            Assert.True(Surface.Shows(window, "Weekly"));
            Assert.Equal(2, Surface.Visible<Meter>(window).Count);

            Assert.True(Surface.Shows(window, "Tokens"));
            Assert.True(Surface.Shows(window, "Input"));
            Assert.True(Surface.Shows(window, "Cache write"));
            Assert.True(Surface.Shows(window, "56.2K"));

            Assert.True(Surface.Shows(window, "Activity"));
            Assert.True(Surface.Shows(window, "claude-opus-5"));
            Assert.True(Surface.Shows(window, "claude-sonnet-5"));
            Assert.False(Surface.Shows(window, UsageFormat.NoActivity));

            Assert.True(Surface.Shows(window, "Integration"));
            Assert.True(Surface.Shows(window, UsageFormat.IntegrationSentence(ProviderStatus.Active)));

            // Nothing on the page names a session, a project or a path.
            Assert.DoesNotContain("session-a", Surface.Lines(window));
            Assert.False(Surface.Shows(window, UsageFormat.ProviderUnavailable));
        }, width: 760d, height: 1600d);
    }

    /// <summary>On screen, a provider that could not be read is the sentence and an enabled retry.</summary>
    [AvaloniaFact]
    public async Task RendersAnErroredProvider()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName, Readings.Failed(ProviderId));
        using ProviderViewModel row = Row(provider);
        var page = new ProviderPageViewModel(row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        var view = new ProviderPageView { DataContext = page };

        Surface.Show(view, window =>
        {
            Assert.True(Surface.Shows(window, UsageFormat.ProviderUnavailable));
            Assert.True(Surface.Shows(window, "Unavailable"));

            // A failed reading carries no metric, so there is no rail to read a level off.
            Assert.Empty(Surface.Visible<Meter>(window));
            Assert.DoesNotContain("0%", Surface.Lines(window));

            Button retry = Surface.Visible<Button>(window)
                .Single(button => string.Equals(button.Content as string, UsageFormat.RetryLabel, StringComparison.Ordinal));
            Assert.True(retry.IsEffectivelyEnabled);
        }, width: 760d, height: 1200d);
    }

    /// <summary>Retrying from the page refreshes the provider and shows what comes back.</summary>
    [Fact]
    public async Task RetryTakesAFreshReading()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName, Readings.Failed(ProviderId));
        using ProviderViewModel row = Row(provider);
        var page = new ProviderPageViewModel(row);

        await page.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(page.Provider.HasError);

        provider.Set(Readings.Healthy(ProviderId));
        await page.Provider.RetryCommand.ExecuteAsync(null);

        Assert.Equal(1, provider.Refreshes);
        Assert.False(page.Provider.HasError);
        Assert.Equal("62%", page.Provider.Metrics[0].PercentText);
    }

    /// <summary>On screen, a provider with no live session says so rather than showing an empty list.</summary>
    [AvaloniaFact]
    public async Task RendersAProviderWithNoActivity()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName);
        using ProviderViewModel row = Row(provider);
        var page = new ProviderPageViewModel(row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        var view = new ProviderPageView { DataContext = page };

        Surface.Show(view, window =>
        {
            Assert.False(page.Provider.HasSessions);
            Assert.True(Surface.Shows(window, UsageFormat.NoActivity));
        }, width: 760d, height: 1400d);
    }

    /// <summary>
    /// On screen, a provider that answered but reported no percentage says so. That is a
    /// different sentence from a failure, and it is never a zero.
    /// </summary>
    [AvaloniaFact]
    public async Task RendersAMetricTheProviderDoesNotReport()
    {
        var provider = new FakeUsageProvider(
            ProviderId,
            ProviderName,
            Readings.Healthy(ProviderId, sessionPercent: null));

        using ProviderViewModel row = Row(provider);
        var page = new ProviderPageViewModel(row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        var view = new ProviderPageView { DataContext = page };

        Surface.Show(view, window =>
        {
            Assert.True(Surface.Shows(window, UsageFormat.MetricUnavailable));
            Assert.False(Surface.Shows(window, "62%"));
            Assert.DoesNotContain("0%", Surface.Lines(window));

            // Both rails are still drawn, and they are drawn differently. The first metric
            // reports nothing, so its rail is the unavailable outline with no fill; the
            // second reports 38, so its rail is a filled one. Asserting only the first left
            // the whole point of the test - that one unreported window does not take the
            // reported one down with it - untested, and an implementation that drew every
            // rail as unavailable passed.
            IReadOnlyList<Meter> meters = Surface.Visible<Meter>(window);
            Assert.Equal(2, meters.Count);

            Assert.True(meters[0].IsUnavailable);
            Assert.Equal(0d, meters[0].RenderedFillWidth);

            Assert.False(meters[1].IsUnavailable);
            Assert.Equal(38d, meters[1].Value);
            Assert.True(
                meters[1].RenderedFillWidth > 0d,
                "The reported rail is drawn with no fill, which reads as a zero.");

            Assert.True(Surface.Shows(window, "38%"));
            Assert.False(Surface.Shows(window, UsageFormat.ProviderUnavailable));
        }, width: 760d, height: 1400d);
    }

    /// <summary>
    /// A provider that reported nothing at all is not a provider that failed, and the page says
    /// the two differently.
    /// </summary>
    [AvaloniaFact]
    public async Task RendersAReadingWithNoMetricAtAll()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName, Readings.NoMetrics(ProviderId));
        using ProviderViewModel row = Row(provider);
        var page = new ProviderPageViewModel(row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        var view = new ProviderPageView { DataContext = page };

        Surface.Show(view, window =>
        {
            Assert.True(Surface.Shows(window, UsageFormat.MetricUnavailable));
            Assert.False(Surface.Shows(window, UsageFormat.ProviderUnavailable));
            Assert.Empty(Surface.Visible<Meter>(window));
            Assert.Empty(Surface.Visible<Button>(window));
        }, width: 760d, height: 1200d);
    }
}
