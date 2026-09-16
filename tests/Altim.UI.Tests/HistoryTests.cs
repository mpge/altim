using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.History;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// History is sparse by design, so the page has two jobs: draw a level that is known but was
/// not re-recorded, and say plainly when nothing is known at all.
/// </summary>
public sealed class HistoryTests
{
    private const string ProviderId = "claude";

    private static ProviderViewModel Row(FakeUsageProvider provider) =>
        new(provider, new TestClock(Readings.Now), AltimSettings.Default);

    private static HistoryViewModel Page(FakeHistoryService history, params ProviderViewModel[] rows) =>
        new(rows, history, new TestClock(Readings.Now));

    /// <summary>A range with no sample at all produces no line.</summary>
    [Fact]
    public void NoSamplesProducesNoLine()
    {
        IReadOnlyList<double?> values = UsageHistorySeries.Build(
            [],
            Readings.Now.AddHours(-24),
            Readings.Now,
            48);

        Assert.Empty(values);
    }

    /// <summary>A level holds until something changes it, rather than dropping to zero.</summary>
    [Fact]
    public void LevelHoldsUntilItChanges()
    {
        DateTimeOffset from = Readings.Now.AddHours(-4);
        UsageSample[] samples =
        [
            Readings.Sample(ProviderId, "five_hour", from.AddMinutes(90), 20d, TimeSpan.FromHours(5), Readings.Now.AddHours(2)),
            Readings.Sample(ProviderId, "five_hour", from.AddMinutes(150), 55d, TimeSpan.FromHours(5), Readings.Now.AddHours(2)),
        ];

        // Four one hour buckets. Nothing was recorded in the first, so nothing is drawn there;
        // the level recorded in the second holds through the fourth because nothing changed it.
        IReadOnlyList<double?> values = UsageHistorySeries.Build(samples, from, Readings.Now, 4);

        Assert.Equal(4, values.Count);
        Assert.Null(values[0]);
        Assert.Equal(20d, values[1]);
        Assert.Equal(55d, values[2]);
        Assert.Equal(55d, values[3]);
    }

    /// <summary>
    /// The carry-in draws the left edge. A range holding no sample is not an empty range when
    /// the level going into it is known.
    /// </summary>
    [Fact]
    public void CarryInDrawsTheLeftEdge()
    {
        DateTimeOffset from = Readings.Now.AddHours(-4);
        UsageSample[] carryIn =
        [
            Readings.Sample(ProviderId, "five_hour", from.AddMinutes(-30), 44d, TimeSpan.FromHours(5), Readings.Now.AddHours(2)),
        ];

        IReadOnlyList<double?> values = UsageHistorySeries.Build(carryIn, from, Readings.Now, 4);

        Assert.Equal(4, values.Count);
        Assert.All(values, value => Assert.Equal(44d, value));
    }

    /// <summary>
    /// A level stops being held once its window has reset: after that instant nobody has
    /// reported anything, and holding the old number would be reporting one that was invented.
    /// </summary>
    [Fact]
    public void LevelStopsAtTheReset()
    {
        DateTimeOffset from = Readings.Now.AddHours(-4);
        UsageSample[] samples =
        [
            Readings.Sample(ProviderId, "five_hour", from.AddHours(1), 70d, TimeSpan.FromHours(5), from.AddHours(2)),
        ];

        IReadOnlyList<double?> values = UsageHistorySeries.Build(samples, from, Readings.Now, 4);

        Assert.Equal(70d, values[1]);
        Assert.Null(values[2]);
        Assert.Null(values[3]);
    }

    /// <summary>A sample that never reported a reset stops being held after one window.</summary>
    [Fact]
    public void LevelStopsAfterOneWindowWhenNoResetWasReported()
    {
        DateTimeOffset from = Readings.Now.AddHours(-24);
        UsageSample[] samples =
        [
            Readings.Sample(ProviderId, "five_hour", from.AddHours(1), 30d, TimeSpan.FromHours(5)),
        ];

        IReadOnlyList<double?> values = UsageHistorySeries.Build(samples, from, Readings.Now, 24);

        Assert.Equal(30d, values[1]);
        Assert.Null(values[^1]);
    }

    /// <summary>The line is drawn from the shortest window, which is the one that moves.</summary>
    [Fact]
    public void PicksTheShortestWindow()
    {
        UsageSample[] samples =
        [
            Readings.Sample(ProviderId, "seven_day", Readings.Now.AddHours(-2), 38d, TimeSpan.FromDays(7)),
            Readings.Sample(ProviderId, "five_hour", Readings.Now.AddHours(-1), 62d, TimeSpan.FromHours(5)),
        ];

        Assert.Equal("five_hour", UsageHistorySeries.SelectPrimaryMetricKey(samples));
        Assert.Null(UsageHistorySeries.SelectPrimaryMetricKey([]));
    }

    /// <summary>An empty store shows the sentence, and no series at all.</summary>
    [Fact]
    public async Task EmptyRangeHasNoSeries()
    {
        var provider = new FakeUsageProvider(ProviderId, "Claude Code");
        using ProviderViewModel row = Row(provider);
        var history = new FakeHistoryService();
        HistoryViewModel page = Page(history, row);

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(page.HasSamples);
        Assert.Empty(page.Series);
        Assert.Equal("No usage recorded yet. Altim starts collecting when an agent runs.", page.EmptyText);

        // The sentence is only true once both questions have been asked. A page that showed it
        // after the range query alone would be claiming nothing is known on the strength of a
        // query that says nothing changed.
        Assert.Equal(1, history.RangeReads);
        Assert.Equal(1, history.CarryInReads);
        Assert.Equal(Readings.Now.AddHours(-24), history.LastCarryInAt);
    }

    /// <summary>
    /// A range holding no sample is not an empty range when the level going into it is known.
    /// This is the case sparse history makes ordinary: a level recorded before the range opened
    /// and never written again, because nothing about it changed.
    /// </summary>
    [Fact]
    public async Task EmptyRangeWithACarryInStillDrawsALine()
    {
        var history = new FakeHistoryService();
        history.Add(Readings.Sample(
            ProviderId,
            "seven_day",
            Readings.Now.AddHours(-30),
            44d,
            TimeSpan.FromDays(7),
            Readings.Now.AddDays(1)));

        // Nothing at all inside the day the page draws.
        Assert.Empty(await history.GetRangeAsync(
            ProviderId,
            Readings.Now.AddHours(-24),
            Readings.Now,
            TestContext.Current.CancellationToken));

        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(history, row);

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.HasSamples);
        UsageTapeSeries series = Assert.Single(page.Series);
        Assert.Equal(HistoryRange.Day.Buckets, series.Values.Count);
        Assert.All(series.Values, value => Assert.Equal(44d, value));
        Assert.Equal(1, history.CarryInReads);
    }

    /// <summary>Every refresh hands the tape a new list, because mutating one does not repaint.</summary>
    [Fact]
    public async Task EachRefreshAssignsANewSeriesList()
    {
        var history = new FakeHistoryService();
        history.Add(Readings.Sample(
            ProviderId, "five_hour", Readings.Now.AddHours(-1), 62d, TimeSpan.FromHours(5), Readings.Now.AddHours(2)));

        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(history, row);

        await page.LoadAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<UsageTapeSeries> first = page.Series;

        await page.LoadAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<UsageTapeSeries> second = page.Series;

        Assert.NotSame(first, second);
        Assert.Single(first);
        Assert.Single(second);
    }

    /// <summary>A store with samples produces one named line per provider.</summary>
    [Fact]
    public async Task SamplesProduceOneLinePerProvider()
    {
        var history = new FakeHistoryService();
        history.Add(
            Readings.Sample(ProviderId, "five_hour", Readings.Now.AddHours(-3), 20d, TimeSpan.FromHours(5), Readings.Now.AddHours(2)),
            Readings.Sample(ProviderId, "five_hour", Readings.Now.AddHours(-1), 62d, TimeSpan.FromHours(5), Readings.Now.AddHours(2)));

        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(history, row);

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.HasSamples);
        UsageTapeSeries series = Assert.Single(page.Series);
        Assert.Equal("Claude Code", series.Name);
        Assert.Equal(UsageTapeEmphasis.Primary, series.Emphasis);
        Assert.Equal(HistoryRange.Day.Buckets, series.Values.Count);
    }

    /// <summary>The three spans are the ones the product documents.</summary>
    [Fact]
    public void OffersThreeSpans()
    {
        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(new FakeHistoryService(), row);

        Assert.Collection(
            page.Ranges,
            range => Assert.Equal("Last 24 hours", range.Name),
            range => Assert.Equal("Last 7 days", range.Name),
            range => Assert.Equal("Last 30 days", range.Name));
        Assert.Equal(HistoryRange.Day, page.SelectedRange);
    }

    /// <summary>A longer span reads further back and redraws at its own resolution.</summary>
    [Fact]
    public async Task ChangingTheSpanRedraws()
    {
        var history = new FakeHistoryService();
        history.Add(Readings.Sample(
            ProviderId,
            "five_hour",
            Readings.Now.AddDays(-5),
            44d,
            TimeSpan.FromDays(7),
            Readings.Now.AddDays(1)));

        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(history, row);

        await page.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(page.HasSamples);
        Assert.Equal(HistoryRange.Day.Buckets, page.Series[0].Values.Count);

        page.SelectedRange = HistoryRange.Week;
        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(page.HasSamples);
        Assert.Equal(HistoryRange.Week.Buckets, page.Series[0].Values.Count);
    }

    /// <summary>Picking a different span reloads the page without anything asking it to.</summary>
    [Fact]
    public async Task ChangingTheSpanReloadsWithoutBeingAsked()
    {
        var history = new FakeHistoryService();
        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(history, row);

        await page.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromHours(24), history.LastSpan);

        history.ExpectQuery();
        page.SelectedRange = HistoryRange.Month;
        await history.Queried.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The new span is read back from the store rather than from the page, so this fails if
        // the picker moved and the query did not.
        Assert.Equal(TimeSpan.FromDays(30), history.LastSpan);
        Assert.Equal(HistoryRange.Month, page.SelectedRange);
    }

    /// <summary>A history store that fails leaves the page empty rather than throwing at it.</summary>
    [Fact]
    public async Task FailedReadLeavesThePageEmpty()
    {
        var history = new FakeHistoryService { Failure = new IOException("locked") };
        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(history, row);

        await page.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(page.HasSamples);
        Assert.Empty(page.Series);
    }

    /// <summary>On screen, an empty range is the sentence and no chart.</summary>
    [AvaloniaFact]
    public void EmptyRangeRendersTheSentence()
    {
        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(new FakeHistoryService(), row);
        var view = new HistoryView { DataContext = page };

        Surface.Show(view, window =>
        {
            UsageTape tape = Assert.Single(Surface.Visible<UsageTape>(window));
            Assert.Empty(tape.Series!);
            Assert.Equal(UsageFormat.HistoryEmpty, tape.EmptyText);
            Assert.DoesNotContain("0%", Surface.Lines(window));
        }, width: 720d, height: 480d);
    }

    /// <summary>On screen, a carry-in draws a line rather than the sentence.</summary>
    [AvaloniaFact]
    public async Task EmptyRangeWithACarryInRendersALine()
    {
        var history = new FakeHistoryService();
        history.Add(Readings.Sample(
            ProviderId,
            "seven_day",
            Readings.Now.AddHours(-30),
            44d,
            TimeSpan.FromDays(7),
            Readings.Now.AddDays(1)));

        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(history, row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        var view = new HistoryView { DataContext = page };

        Surface.Show(view, window =>
        {
            UsageTape tape = Assert.Single(Surface.Visible<UsageTape>(window));
            UsageTapeSeries series = Assert.Single(tape.Series!);
            Assert.Equal("Claude Code", series.Name);
            Assert.All(series.Values, value => Assert.Equal(44d, value));
        }, width: 720d, height: 480d);
    }

    /// <summary>On screen, a range with samples hands the tape a line to draw.</summary>
    [AvaloniaFact]
    public async Task RangeWithSamplesRendersALine()
    {
        var history = new FakeHistoryService();
        history.Add(
            Readings.Sample(ProviderId, "five_hour", Readings.Now.AddHours(-3), 20d, TimeSpan.FromHours(5), Readings.Now.AddHours(2)),
            Readings.Sample(ProviderId, "five_hour", Readings.Now.AddHours(-1), 62d, TimeSpan.FromHours(5), Readings.Now.AddHours(2)));

        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(history, row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        var view = new HistoryView { DataContext = page };

        Surface.Show(view, window =>
        {
            UsageTape tape = Assert.Single(Surface.Visible<UsageTape>(window));
            UsageTapeSeries series = Assert.Single(tape.Series!);
            Assert.Equal("Claude Code", series.Name);
        }, width: 720d, height: 480d);
    }
}
