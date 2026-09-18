using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.History;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
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

    /// <summary>
    /// The page can be scrolled to its bottom in a window too short to hold it.
    /// </summary>
    /// <remarks>
    /// It could not. The root was a <c>Grid</c>, so anything past the window's lower edge was
    /// simply unreachable, and Overview and Settings had each been wrapped in a scroll viewer
    /// while this page was not. Nothing caught it because every test here either measured the
    /// view models or rendered into a window tall enough for the whole page. The map hid it
    /// too: it was narrow and short until its squares were shared out across the panel's full
    /// width, and then the tape below it went off the bottom.
    ///
    /// The assertion is that the content is genuinely taller than the viewport and that the
    /// viewer will move — an extent equal to its viewport would satisfy "a scroll viewer is
    /// present" while leaving the page exactly as unreachable as before.
    /// </remarks>
    [AvaloniaFact]
    public void TheWholePageCanBeReachedInAWindowTooShortToHoldIt()
    {
        var provider = new FakeUsageProvider(ProviderId, "Claude Code");
        using ProviderViewModel row = Row(provider);
        HistoryViewModel page = Page(new FakeHistoryService(), row);
        var view = new HistoryView { DataContext = page };

        Surface.Show(view, window =>
        {
            ScrollViewer scroller = Assert.Single(
                window.GetVisualDescendants().OfType<ScrollViewer>(),
                v => v.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled);

            Assert.True(
                scroller.Extent.Height > scroller.Viewport.Height,
                $"The page is {scroller.Extent.Height} tall in a {scroller.Viewport.Height} viewport, "
                + "so this window does not exercise scrolling at all.");

            scroller.ScrollToEnd();

            Assert.True(
                scroller.Offset.Y > 0d,
                "The page did not move, so its lower half is unreachable.");
        }, width: 760d, height: 320d);
    }

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
    /// <remarks>
    /// The tape draws its own text, so the sentence never becomes a <c>TextBlock</c> and
    /// <see cref="Surface.Shows"/> cannot see it. This used to assert <c>Series</c> and
    /// <c>EmptyText</c> - two CLR properties - while the sentence above it claimed something
    /// about the picture. A tape that read an empty series and drew a flat line at zero
    /// anyway satisfied every one of those assertions. The picture is read here instead:
    /// nothing anywhere near the line colour, and ink in the label colour where the sentence
    /// is set.
    /// </remarks>
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

            Frame frame = Frame.Capture(window);
            Rect plot = BoundsIn(tape, window);

            // No chart. The line is the darkest thing the tape ever draws and the sentence
            // is set in the label colour, so a tolerance this wide still separates them.
            Assert.Equal(
                0,
                frame.Count(plot, colour => Ink.Near(colour, Token(window, LinePrimary), 24)));

            // And the sentence is genuinely on the screen, not merely on the control.
            Assert.True(
                frame.Count(plot, colour => Ink.Near(colour, Token(window, Label), 24)) > 0,
                $"Nothing in the label colour was drawn in {plot}: {frame.Describe(plot, 8)}.");
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

            AssertDrewALine(window, tape);
        }, width: 720d, height: 480d);
    }

    /// <summary>
    /// On screen, the map fills the panel it is in rather than standing at a fixed width in a
    /// wider card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the page level half of the fit, and the half a control test cannot reach. The
    /// map sits in a horizontally scrolling <c>ScrollViewer</c>, and such a parent offers its
    /// content infinity rather than a width - that is exactly what lets the content be wider
    /// than the viewport. A year of week columns cannot be shared out of infinity, so the page
    /// states the viewport as the map's <c>MinWidth</c>, which bounds the fit without capping
    /// what the map may ask for.
    /// </para>
    /// <para>
    /// Without that statement the map falls back to its smallest square and leaves a third of
    /// the panel empty, which is the defect this was written for. The assertion is that no
    /// larger square would have fitted, rather than that the square is any particular size:
    /// the square is the theme's business and filling the panel is the page's.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public async Task TheMapFillsThePanelItIsIn()
    {
        var history = new FakeHistoryService();
        for (int back = 0; back < 40; back++)
        {
            history.AddDays(new UsageDay(
                ProviderId,
                new DateOnly(2026, 9, 15).AddDays(-back),
                new TokenTotals((back + 1) * 1_000L, null, null, null),
                null,
                UsageDaySource.Observed,
                DateTimeOffset.UnixEpoch));
        }

        using ProviderViewModel row = Row(new FakeUsageProvider(ProviderId, "Claude Code"));
        HistoryViewModel page = Page(history, row);
        await page.LoadAsync(TestContext.Current.CancellationToken);

        var view = new HistoryView { DataContext = page };

        Surface.Show(view, window =>
        {
            UsageMap map = Assert.Single(Surface.Visible<UsageMap>(window));
            ScrollViewer scroller = Assert.IsType<ScrollViewer>(
                map.FindAncestorOfType<ScrollViewer>(),
                exactMatch: false);

            Assert.True(scroller.Viewport.Width > 0d, "The panel has no width to fill.");
            Assert.Equal(scroller.Viewport.Width, map.MinWidth, 6);

            double cell = 0d;
            double drawnTo = 0d;
            int columns = 0;
            HashSet<double> lefts = [];

            foreach (UsageMapCell day in map.Rows![0].Cells)
            {
                if (map.CellBounds(0, day.Day) is not { } square)
                {
                    continue;
                }

                cell = square.Width;
                drawnTo = Math.Max(drawnTo, square.Right);
                lefts.Add(square.X);
            }

            columns = lefts.Count;
            Assert.True(columns > 50, $"A year should be fifty three columns and drew {columns}.");

            drawnTo += map.FocusRingWidth;
            double oneMore = drawnTo + 1d + map.CellGap;

            Assert.True(
                drawnTo <= scroller.Viewport.Width,
                $"The grid ran {drawnTo} past a {scroller.Viewport.Width} panel.");
            Assert.True(
                oneMore > scroller.Viewport.Width,
                $"The grid stopped at {drawnTo} of {scroller.Viewport.Width} with a {cell} square, "
                    + "which leaves room for a larger one.");
        }, width: 860d, height: 900d);
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

            AssertDrewALine(window, tape);
        }, width: 720d, height: 480d);
    }

    /// <summary>The tape's line colour, which nothing else on the tape is drawn in.</summary>
    private const string LinePrimary = "AltimChartLinePrimaryBrush";

    /// <summary>The colour the tape sets its own text in, the empty sentence included.</summary>
    private const string Label = "AltimChartLabelBrush";

    /// <summary>
    /// Asserts the tape drew a line rather than falling back to its empty sentence, which is
    /// the one thing a <c>Series</c> that holds values cannot say on its own: the values can
    /// be right and the picture still be the sentence.
    /// </summary>
    /// <param name="window">The window the page is hosted in.</param>
    /// <param name="tape">The tape on the page.</param>
    private static void AssertDrewALine(Window window, UsageTape tape)
    {
        Frame frame = Frame.Capture(window);
        Rect plot = BoundsIn(tape, window);

        int drawn = frame.Count(plot, colour => Ink.Near(colour, Token(window, LinePrimary), 4));
        Assert.True(
            drawn > 0,
            $"No line was drawn in {plot}, so the tape is showing its empty sentence: "
                + $"{frame.Describe(plot, 8)}.");
    }

    /// <summary>A visual's bounds in the coordinates of the window holding it.</summary>
    /// <param name="visual">The visual to locate.</param>
    /// <param name="window">The window it is hosted in.</param>
    /// <returns>The bounds.</returns>
    private static Rect BoundsIn(Visual visual, Window window)
    {
        Point? origin = visual.TranslatePoint(default, window);
        Assert.True(origin is not null, "The visual is not in this window.");
        return new Rect(origin!.Value, visual.Bounds.Size);
    }

    /// <summary>A design system colour, as the renderer produces it under this window.</summary>
    /// <param name="window">The window whose variant to resolve under.</param>
    /// <param name="key">The resource key.</param>
    /// <returns>The colour.</returns>
    private static Color Token(Window window, string key)
    {
        Assert.True(
            DesignSystem.Ensure().TryGetResource(key, window.ActualThemeVariant, out object? value),
            $"{key} does not resolve under {window.ActualThemeVariant}.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
