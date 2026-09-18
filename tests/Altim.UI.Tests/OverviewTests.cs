using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// What Overview does with the readings that arrive after it has been drawn.
/// </summary>
/// <remarks>
/// <para>
/// The page is built once, when the window opens, and then lives for as long as somebody
/// leaves it open. Everything on it that follows the provider's own <c>UsageChanged</c> event
/// was correct; everything that was only ever computed by the page's own load was not, and the
/// two failures look identical in a screenshot. Both are here.
/// </para>
/// <para>
/// A reading is applied through <see cref="ProviderViewModel.Apply"/>, which is the method the
/// provider event lands on once it has reached the dispatcher. The follow-up work it starts is
/// awaited through <see cref="OverviewViewModel.FollowingReading"/> rather than looked at after
/// a sleep: it goes to the thread pool, and "it will have finished by now" is a bet on somebody
/// else's machine.
/// </para>
/// </remarks>
public sealed class OverviewTests
{
    private const string ProviderId = "claude";
    private const string ProviderName = "Claude Code";

    private static ProviderViewModel Row(FakeUsageProvider provider) =>
        new(provider, new TestClock(Readings.Now), AltimSettings.Default);

    private static AgentSession Working(string id = "session-a") => Readings.Session(
        ProviderId,
        id,
        "claude-opus-5",
        Readings.Now.AddMinutes(-40),
        Readings.Now.AddMinutes(-2),
        isActive: true,
        new TokenTotals(18_400, 3_200, null, null));

    /// <summary>
    /// A sample of the paced window, taken just before the instant one window back, so the
    /// comparison has an answer: 62 now against 50 then is +12.
    /// </summary>
    private static FakeHistoryService HistoryWithACarryIn()
    {
        var history = new FakeHistoryService();
        history.Add(Readings.Sample(
            ProviderId,
            "five_hour",
            Readings.Now.AddHours(-5).AddMinutes(-10),
            50d,
            TimeSpan.FromHours(5),
            Readings.Now.AddHours(-4)));

        return history;
    }

    /// <summary>
    /// <b>Pacing survives the readings that arrive after the page was loaded.</b>
    /// </summary>
    /// <remarks>
    /// Every reading rebuilds the row's metrics, and the row drops the comparison as it does
    /// so, because the figure it was computed from has just moved. Nothing then computed a new
    /// one: pacing was restored only by the page's own load, and the page loads once, when the
    /// window is built. So a minute after the window opened every card fell to an em dash and
    /// stayed there until the window was closed and opened again. The em dash means "not
    /// enough local history to compare" - which was false, since the history was there and the
    /// comparison had been made a minute earlier.
    /// </remarks>
    [Fact]
    public async Task PacingSurvivesAReadingThatArrivesAfterTheLoad()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName);
        using ProviderViewModel row = Row(provider);
        using var page = new OverviewViewModel([row], HistoryWithACarryIn(), new TestClock(Readings.Now));

        await page.LoadAsync(TestContext.Current.CancellationToken);
        await page.FollowingReading;

        Assert.Equal("+12%", row.PacingText);
        Assert.Equal(12d, row.PacingDelta);

        // A minute later the scheduler takes another reading and hands it to the row.
        row.Apply(Readings.Healthy(ProviderId));
        await page.FollowingReading;

        Assert.Equal("+12%", row.PacingText);
        Assert.Equal(12d, row.PacingDelta);
    }

    /// <summary>And a reading that moves the figure moves the comparison with it.</summary>
    [Fact]
    public async Task PacingFollowsTheFigureItCompares()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName);
        using ProviderViewModel row = Row(provider);
        using var page = new OverviewViewModel([row], HistoryWithACarryIn(), new TestClock(Readings.Now));

        await page.LoadAsync(TestContext.Current.CancellationToken);
        await page.FollowingReading;
        Assert.Equal("+12%", row.PacingText);

        // 70 now against the same 50 an hour into the previous window.
        row.Apply(Readings.Healthy(ProviderId, 70d));
        await page.FollowingReading;

        Assert.Equal("+20%", row.PacingText);
        Assert.Equal(20d, row.PacingDelta);
    }

    /// <summary>
    /// A provider with no history behind it is an em dash, and that is what the em dash is
    /// for. It is here so the test above cannot pass by never producing one.
    /// </summary>
    [Fact]
    public async Task PacingWithNoHistoryIsTheEmDash()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName);
        using ProviderViewModel row = Row(provider);
        using var page = new OverviewViewModel([row], new FakeHistoryService(), new TestClock(Readings.Now));

        await page.LoadAsync(TestContext.Current.CancellationToken);
        await page.FollowingReading;

        Assert.Null(row.PacingDelta);
        Assert.Equal(UsageFormat.PacingUnknown, row.PacingText);
    }

    /// <summary>
    /// <b>The activity panel follows every reading, which is what its badge claims.</b>
    /// </summary>
    /// <remarks>
    /// The panel is badged "Live" and its view's own comment justifies the badge by saying
    /// activity is read live. It was read once, when the window opened, so an agent that
    /// started and finished while somebody sat on the page never appeared on it at all. The
    /// badge was the only thing on the panel that was wrong, and it was wrong in the direction
    /// that stops a reader looking again.
    /// </remarks>
    [Fact]
    public async Task TheActivityPanelFollowsEveryReading()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName);
        using ProviderViewModel row = Row(provider);
        using var page = new OverviewViewModel([row], new FakeHistoryService(), new TestClock(Readings.Now));

        await page.LoadAsync(TestContext.Current.CancellationToken);
        await page.FollowingReading;

        Assert.Empty(page.Activity);
        Assert.False(page.HasActivity);
        Assert.Equal("Live", page.LiveLabel);

        // An agent starts while the page is open, and the next reading brings it.
        provider.Sessions = [Working()];
        row.Apply(Readings.Healthy(ProviderId));
        await page.FollowingReading;

        AgentSessionViewModel session = Assert.Single(page.Activity);
        Assert.Equal("claude-opus-5", session.Title);
        Assert.Equal(ProviderName, session.ProviderName);
        Assert.True(page.HasActivity);

        // And it finishes while the page is still open.
        provider.Sessions = [];
        row.Apply(Readings.Healthy(ProviderId, 63d));
        await page.FollowingReading;

        Assert.Empty(page.Activity);
        Assert.False(page.HasActivity);
    }

    /// <summary>
    /// The list is left alone when nothing a reader can see has changed.
    /// </summary>
    /// <remarks>
    /// Following every reading means this runs every few seconds, and emptying an observable
    /// collection and refilling it throws away the list's item containers: a panel that did
    /// that would blink, and would throw away the reader's scroll position while they were
    /// reading it. The rows are snapshots rebuilt from scratch on every reading, so the
    /// comparison is on what a row prints rather than on which instance it is.
    /// </remarks>
    [Fact]
    public async Task TheActivityListIsLeftAloneWhenNothingHasChanged()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName) { Sessions = [Working()] };
        using ProviderViewModel row = Row(provider);
        using var page = new OverviewViewModel([row], new FakeHistoryService(), new TestClock(Readings.Now));

        await page.LoadAsync(TestContext.Current.CancellationToken);
        await page.FollowingReading;

        AgentSessionViewModel first = Assert.Single(page.Activity);

        // The same session, reported again by the next reading. The provider hands back a new
        // AgentSession instance and the row builds a new view model from it, so nothing about
        // this is reference equality doing the work.
        provider.Sessions = [Working()];
        row.Apply(Readings.Healthy(ProviderId, 63d));
        await page.FollowingReading;

        Assert.Same(first, Assert.Single(page.Activity));
    }

    /// <summary>
    /// A reading that failed takes that provider's rows off the activity panel too, because
    /// the row it reads them from drops them.
    /// </summary>
    [Fact]
    public async Task AFailedReadingClearsThatProvidersActivity()
    {
        var provider = new FakeUsageProvider(ProviderId, ProviderName) { Sessions = [Working()] };
        using ProviderViewModel row = Row(provider);
        using var page = new OverviewViewModel([row], new FakeHistoryService(), new TestClock(Readings.Now));

        await page.LoadAsync(TestContext.Current.CancellationToken);
        await page.FollowingReading;
        Assert.True(page.HasActivity);

        // The provider still has a session to hand out - a usage read that threw leaves the
        // sessions it last saw in place - so the row is what has to refuse it.
        row.Apply(Readings.Failed(ProviderId));
        await page.FollowingReading;

        Assert.Empty(page.Activity);
        Assert.False(page.HasActivity);
        Assert.True(row.HasError);
    }
}
