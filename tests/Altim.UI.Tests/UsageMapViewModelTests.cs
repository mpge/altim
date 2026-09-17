using System.Diagnostics;
using System.Globalization;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.History;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The map's view model: a year of squares per provider, a combined row under them, and the
/// words that go with each square.
/// </summary>
/// <remarks>
/// <para>
/// Two rules are load bearing here and both are asserted rather than assumed. The first is
/// that <see cref="UsageMapCell.IsKnown"/> follows the token figure and not the row: a day
/// with a row carrying a peak and no tokens must come out of the view model as an unknown
/// square, because the square draws what the day cost and nobody can say what that was, while
/// a day a per-day source reported as nothing at all is known and fills at the ramp's foot.
/// </para>
/// <para>
/// The second is that percentages are never combined. Forty per cent of one vendor's window
/// and forty per cent of another's are measured against different limits, so adding or
/// averaging them invents a figure nobody reported. Tokens sum; percentages are listed one
/// provider to a line and the combined square carries none at all.
/// </para>
/// </remarks>
public sealed class UsageMapViewModelTests
{
    private const string ClaudeId = "claude";
    private const string CodexId = "codex";
    private const string ClaudeName = "Claude Code";
    private const string CodexName = "Codex";

    /// <summary>The local day the test clock reads, which is the map's right hand edge.</summary>
    private static readonly DateOnly Today = new(2026, 9, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The combined row sums the token figures of the providers that knew the day.</summary>
    [Fact]
    public async Task TheCombinedRowSumsTokensAcrossProviders()
    {
        var history = new FakeHistoryService();
        history.AddDays(
            Day(ClaudeId, Today, input: 10, output: 5, cacheRead: 100, cacheWrite: 3),
            Day(CodexId, Today, input: 7, output: 1, cacheRead: 0, cacheWrite: null));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        using ProviderViewModel codex = Provider(CodexId, CodexName);
        UsageMapViewModel map = Map(history, claude, codex);

        await map.LoadAsync(Ct);

        UsageMapRow combined = Assert.IsType<UsageMapRow>(map.CombinedRow);
        UsageMapCell cell = CellFor(combined, Today);

        Assert.True(cell.IsKnown);
        Assert.Equal(126L, cell.Tokens);
    }

    /// <summary>
    /// A day one provider knows and another does not is known in the combined row, and the
    /// sum is then not a total across every provider. The tooltip has to say so: it names who
    /// the figure covers and who had nothing, so the number is not read as a whole.
    /// </summary>
    [Fact]
    public async Task ADayOnlyOneProviderKnowsIsKnownAndTheTooltipSaysWhoContributed()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today, input: 10, output: 5, cacheRead: 100, cacheWrite: 3));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        using ProviderViewModel codex = Provider(CodexId, CodexName);
        UsageMapViewModel map = Map(history, claude, codex);

        await map.LoadAsync(Ct);

        UsageMapCell cell = CellFor(map.CombinedRow!, Today);
        Assert.True(cell.IsKnown);
        Assert.Equal(118L, cell.Tokens);

        string[] lines = Lines(cell);
        Assert.Contains("118 tokens from Claude Code", lines);
        Assert.Contains("No data from Codex", lines);
        Assert.DoesNotContain("118 tokens from Claude Code and Codex", lines);
    }

    /// <summary>A day no provider knows is unknown in the combined row, not a zero.</summary>
    [Fact]
    public async Task ADayNoProviderKnowsStaysUnknownInTheCombinedRow()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        using ProviderViewModel codex = Provider(CodexId, CodexName);
        UsageMapViewModel map = Map(history, claude, codex);

        await map.LoadAsync(Ct);

        UsageMapCell cell = CellFor(map.CombinedRow!, Today.AddDays(-1));
        Assert.False(cell.IsKnown);
        Assert.Null(cell.Tokens);
        Assert.Contains("No data for this day", Lines(cell));
    }

    /// <summary>
    /// The combined tooltip lists each provider's peak on its own line, and the combined
    /// square carries no percentage of its own. Forty and eighty are neither one hundred and
    /// twenty nor sixty: both of those would be figures nobody reported.
    /// </summary>
    [Fact]
    public async Task TheCombinedTooltipListsEachProvidersPeakAndNeverCombinesThem()
    {
        var history = new FakeHistoryService();
        history.AddDays(
            Day(ClaudeId, Today, peak: 40d),
            Day(CodexId, Today, peak: 80d));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        using ProviderViewModel codex = Provider(CodexId, CodexName);
        UsageMapViewModel map = Map(history, claude, codex);

        await map.LoadAsync(Ct);

        UsageMapCell cell = CellFor(map.CombinedRow!, Today);
        string[] lines = Lines(cell);

        Assert.Null(cell.PeakPercent);
        Assert.Contains("Claude Code peak 40%", lines);
        Assert.Contains("Codex peak 80%", lines);
        Assert.DoesNotContain("120%", cell.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("60%", cell.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A day with a peak and no token figure is a day Altim knows how close to the limit the
    /// user came on, and nothing about what they spent. The square is what the spend looks
    /// like, so it is drawn as unknown — an outline, not the faintest fill, which would claim
    /// the day cost nothing. The peak is not lost: it is still in the words beside it.
    /// </summary>
    /// <remarks>
    /// This is the ordinary shape of a day outside a provider's backfill reach. The rollup
    /// writes exactly this row for every day it sees, because a reading's token totals are
    /// running totals and never that day's spend.
    /// </remarks>
    [Fact]
    public async Task AStoredDayWithAPeakAndNoTokensDrawsAsUnknownAndStillNamesItsPeak()
    {
        var history = new FakeHistoryService();
        history.AddDays(new UsageDay(
            ClaudeId, Today, Tokens: null, PeakPercent: 40d, UsageDaySource.Observed, DateTimeOffset.UnixEpoch));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        using ProviderViewModel codex = Provider(CodexId, CodexName);
        UsageMapViewModel map = Map(history, claude, codex);

        await map.LoadAsync(Ct);

        UsageMapCell cell = CellFor(map.Rows![0], Today);
        Assert.False(cell.IsKnown);
        Assert.Null(cell.Tokens);

        string[] lines = Lines(cell);
        Assert.Contains("No token figures reported", lines);
        Assert.Contains("Peak 40%", lines);

        // And it reaches the combined row the same way round: nothing was spent that anyone
        // can name, so there is nothing to add up.
        UsageMapCell combined = CellFor(map.CombinedRow!, Today);
        Assert.False(combined.IsKnown);
        Assert.Null(combined.Tokens);
    }

    /// <summary>
    /// The distinction the previous test rests on: a square is unknown because no token
    /// figure is known, never because the row is missing. A day with a peak and no tokens and
    /// a day with no row at all are both outlines, and only the words tell them apart.
    /// </summary>
    [Fact]
    public async Task AZeroTokenDayIsStillKnownAndIsNotTheSameAsAPeakWithNoTokens()
    {
        var history = new FakeHistoryService();
        history.AddDays(
            new UsageDay(ClaudeId, Today, new TokenTotals(0, 0, 0, 0), PeakPercent: 40d,
                         UsageDaySource.Backfilled, DateTimeOffset.UnixEpoch),
            new UsageDay(ClaudeId, Today.AddDays(-1), Tokens: null, PeakPercent: 40d,
                         UsageDaySource.Observed, DateTimeOffset.UnixEpoch));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        UsageMapViewModel map = Map(history, claude);

        await map.LoadAsync(Ct);

        UsageMapCell zero = CellFor(map.Rows![0], Today);
        Assert.True(zero.IsKnown);
        Assert.Equal(0L, zero.Tokens);

        Assert.False(CellFor(map.Rows[0], Today.AddDays(-1)).IsKnown);
    }

    /// <summary>
    /// The provider tooltip breaks the day into the four components it was reported in, names
    /// the day's peak, and says whether Altim watched the day or read it from the provider's
    /// own history.
    /// </summary>
    [Fact]
    public async Task TheProviderTooltipBreaksOutTheComponentsAndSaysWhereTheDayCameFrom()
    {
        var history = new FakeHistoryService();
        history.AddDays(
            Day(ClaudeId, Today, input: 10, output: 5, cacheRead: 100, cacheWrite: 3, peak: 40d),
            Day(ClaudeId, Today.AddDays(-1), input: 9, output: null, cacheRead: null, cacheWrite: null,
                peak: null, source: UsageDaySource.Backfilled));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        UsageMapViewModel map = Map(history, claude);

        await map.LoadAsync(Ct);

        string[] observed = Lines(CellFor(map.Rows![0], Today));
        Assert.Equal(Today.ToString("D", CultureInfo.CurrentCulture), observed[0]);
        Assert.Contains("118 tokens", observed);
        Assert.Contains("Input 10 · Output 5 · Cache read 100 · Cache write 3", observed);
        Assert.Contains("Peak 40%", observed);
        Assert.Contains("Observed by Altim", observed);

        // A component the provider did not report is absent rather than shown as a zero, and
        // a day read back from the provider says so instead of claiming Altim watched it.
        string[] backfilled = Lines(CellFor(map.Rows[0], Today.AddDays(-1)));
        Assert.Contains("Input 9", backfilled);
        Assert.DoesNotContain("Output", string.Join('\n', backfilled), StringComparison.Ordinal);
        Assert.Contains("Peak not reported", backfilled);
        Assert.Contains("Backfilled from Claude Code", backfilled);
    }

    /// <summary>
    /// Every value on screen is ranked together, the combined row included, so a square in one
    /// row means the same thing as a square in another.
    /// </summary>
    /// <remarks>
    /// Five days, two providers, the same figures in each: the provider days are 10 to 50 and
    /// the combined days are 20 to 100. Ranked over all twenty values the heaviest provider
    /// day is one step below the top, because the combined days stand above it. A scale built
    /// from the provider rows alone would put that day at the top, which is what this asserts
    /// against.
    /// </remarks>
    [Fact]
    public async Task TheScaleRanksEveryValueOnScreenIncludingTheCombinedRow()
    {
        var history = new FakeHistoryService();
        for (int i = 0; i < 5; i++)
        {
            long tokens = (i + 1) * 10L;
            history.AddDays(
                Tokens(ClaudeId, Today.AddDays(-i), tokens),
                Tokens(CodexId, Today.AddDays(-i), tokens));
        }

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        using ProviderViewModel codex = Provider(CodexId, CodexName);
        UsageMapViewModel map = Map(history, claude, codex);

        await map.LoadAsync(Ct);

        UsageMapScale scale = Assert.IsType<UsageMapScale>(map.Scale);
        Assert.Equal(4, scale.LevelFor(100L));
        Assert.Equal(3, scale.LevelFor(50L));
        Assert.Equal(0, scale.LevelFor(10L));
        Assert.Equal(UsageMapScale.Unknown, scale.LevelFor(null));
    }

    /// <summary>
    /// One row per provider in the order they were given, then the combined row, marked as the
    /// total so the map can rule a separator above it rather than stacking it in with its own
    /// parts.
    /// </summary>
    [Fact]
    public async Task TheCombinedRowComesLastAndIsMarkedAsTheTotal()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        using ProviderViewModel codex = Provider(CodexId, CodexName);
        UsageMapViewModel map = Map(history, claude, codex);

        await map.LoadAsync(Ct);

        Assert.Collection(
            map.Rows!,
            row => Assert.Equal(ClaudeName, row.Name),
            row => Assert.Equal(CodexName, row.Name),
            row => Assert.Equal("Combined", row.Name));

        Assert.False(map.Rows![0].IsCombined);
        Assert.False(map.Rows[1].IsCombined);
        Assert.True(map.Rows[2].IsCombined);
        Assert.Same(map.Rows[2], map.CombinedRow);
    }

    /// <summary>
    /// One provider gets no combined row. A total over a single provider is that provider's
    /// own year written out twice, and a second identical strip under a separator reads as a
    /// second provider rather than as a sum.
    /// </summary>
    [Fact]
    public async Task OneProviderGetsNoCombinedRowBecauseThereIsNothingToAdd()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        UsageMapViewModel map = Map(history, claude);

        await map.LoadAsync(Ct);

        UsageMapRow row = Assert.Single(map.Rows!);
        Assert.Equal(ClaudeName, row.Name);
        Assert.False(row.IsCombined);
        Assert.Null(map.CombinedRow);
    }

    /// <summary>
    /// A year of squares per row, ending today. A day with no stored row is present and
    /// unknown rather than missing, because an absent square is a day outside the map and an
    /// unknown one is a day inside it that nothing is known about.
    /// </summary>
    [Fact]
    public async Task EveryDayOfTheYearGetsASquareWhetherOrNotItIsKnown()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        UsageMapViewModel map = Map(history, claude);

        await map.LoadAsync(Ct);

        foreach (UsageMapRow row in map.Rows!)
        {
            Assert.Equal(365, row.Cells.Count);
            Assert.Equal(Today.AddDays(-364), row.Cells[0].Day);
            Assert.Equal(Today, row.Cells[^1].Day);
        }

        Assert.True(CellFor(map.Rows[0], Today).IsKnown);
        Assert.False(CellFor(map.Rows[0], Today.AddDays(-364)).IsKnown);
    }

    /// <summary>
    /// Nothing is read in the constructor, and a store that has not answered yet leaves the
    /// map silent rather than announcing an empty history it was never handed.
    /// </summary>
    /// <remarks>
    /// The absence of a read is waited for rather than sampled. A load handed to the thread
    /// pool has not necessarily reached it by the next statement, so a counter read straight
    /// after the constructor would report zero whether or not a load had been started, and the
    /// assertion would be checking nothing at all.
    /// </remarks>
    [Fact]
    public async Task ConstructionReadsNothingAndASlowStoreDoesNotBlockIt()
    {
        using var entered = new ManualResetEventSlim(initialState: false);
        var history = new FakeHistoryService
        {
            DayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            DayEntered = entered,
        };

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);

        long before = Stopwatch.GetTimestamp();
        UsageMapViewModel map = Map(history, claude);
        TimeSpan spent = Stopwatch.GetElapsedTime(before);

        Assert.True(spent < TimeSpan.FromSeconds(1), $"The constructor blocked for {spent}.");
        Assert.False(entered.Wait(TimeSpan.FromMilliseconds(500), Ct), "The constructor started a read.");
        Assert.Equal(0, history.DayReads);
        Assert.Null(map.Rows);

        Task load = map.LoadAsync(Ct);

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct), "The load never reached the store.");
        Assert.False(load.IsCompleted);
        Assert.Null(map.Rows);

        history.DayGate.SetResult();
        await load;

        Assert.NotNull(map.Rows);
    }

    /// <summary>
    /// A store that cannot be read leaves the map silent. Saying "no usage recorded yet" on
    /// the strength of a read that failed would be the same lie as painting an unknown day as
    /// a zero: we were not told the history is empty, we were not told anything.
    /// </summary>
    [Fact]
    public async Task AFailedReadLeavesTheMapSilentRatherThanClaimingTheHistoryIsEmpty()
    {
        var history = new FakeHistoryService { Failure = new IOException("locked") };
        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        UsageMapViewModel map = Map(history, claude);

        await map.LoadAsync(Ct);

        Assert.Null(map.Rows);
        Assert.Null(map.CombinedRow);
    }

    /// <summary>
    /// One provider's store failing does not take the map down with it. That provider's year
    /// is unknown, which is true, the other provider's is drawn, and the combined row says
    /// plainly that its figure covers only the one that answered.
    /// </summary>
    [Fact]
    public async Task AProviderWhoseYearCouldNotBeReadKeepsARowOfUnknownSquares()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today));
        history.UnreadableProviders.Add(CodexId);

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        using ProviderViewModel codex = Provider(CodexId, CodexName);
        UsageMapViewModel map = Map(history, claude, codex);

        await map.LoadAsync(Ct);

        Assert.NotNull(map.Rows);
        Assert.True(CellFor(map.Rows![0], Today).IsKnown);
        Assert.All(map.Rows[1].Cells, cell => Assert.False(cell.IsKnown));

        UsageMapCell combined = CellFor(map.CombinedRow!, Today);
        Assert.True(combined.IsKnown);
        Assert.Contains("No data from Codex", Lines(combined));
    }

    /// <summary>
    /// A store that answers and has nothing in it is a different state: the rows exist, every
    /// square is unknown, and the sentence the product already documents stands in for the
    /// grid. The map does not invent copy of its own for it.
    /// </summary>
    [Fact]
    public async Task AStoreWithNoDaysAtAllCarriesTheDocumentedEmptySentence()
    {
        var history = new FakeHistoryService();
        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        UsageMapViewModel map = Map(history, claude);

        await map.LoadAsync(Ct);

        Assert.Equal(UsageFormat.HistoryEmpty, map.EmptyText);
        Assert.NotNull(map.Rows);
        Assert.All(map.Rows!, row => Assert.All(row.Cells, cell => Assert.False(cell.IsKnown)));
    }

    /// <summary>Every load hands the map a new list, because mutating one does not repaint.</summary>
    [Fact]
    public async Task EachLoadAssignsANewRowsList()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        UsageMapViewModel map = Map(history, claude);

        await map.LoadAsync(Ct);
        IReadOnlyList<UsageMapRow> first = map.Rows!;

        await map.LoadAsync(Ct);

        Assert.NotSame(first, map.Rows);
    }

    /// <summary>
    /// A year of days for every provider, then a thousand cells and a quantile scale over
    /// them, is the work the History page does every time it is opened. None of it runs on the
    /// dispatcher thread.
    /// </summary>
    [AvaloniaFact]
    public async Task AYearOfDaysIsNotReadOnTheDispatcherThread()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        UsageMapViewModel map = Map(history, claude);

        Assert.True(Dispatcher.UIThread.CheckAccess());
        int dispatcher = Environment.CurrentManagedThreadId;

        await map.LoadAsync(Ct);

        Assert.NotNull(history.DaysReadOnThreadId);
        Assert.NotEqual(dispatcher, history.DaysReadOnThreadId);
    }

    /// <summary>
    /// The map draws its grid rather than building it out of controls, so to an assistive
    /// technology it is one element. The peer is what makes it more than that: one child per
    /// square, each with a name reading the row, the date and the value, and the square's
    /// tooltip as its help text. Without this the whole year is a single unlabelled rectangle.
    /// </summary>
    [AvaloniaFact]
    public async Task EverySquareCarriesAnAccessibleNameAndItsTooltipAsHelpText()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today, input: 10, output: 5, cacheRead: 100, cacheWrite: 3));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        using ProviderViewModel codex = Provider(CodexId, CodexName);
        UsageMapViewModel model = Map(history, claude, codex);
        await model.LoadAsync(Ct);

        var map = new UsageMap { Rows = model.Rows, Scale = model.Scale };
        using PixelHost host = PixelHost.Show(map, ThemeVariant.Light, width: 900d, height: 340d);

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(map);
        IReadOnlyList<AutomationPeer> squares = peer.GetChildren();

        // Three rows of a year, and not one of them nameless.
        Assert.Equal(1095, squares.Count);
        Assert.All(squares, square => Assert.False(string.IsNullOrWhiteSpace(square.GetName())));

        string today = Today.ToString("D", CultureInfo.CurrentCulture);
        AutomationPeer known = Assert.Single(
            squares,
            square => square.GetName() == $"Claude Code, {today}, 118 tokens");

        Assert.Equal(CellFor(model.Rows![0], Today).Detail, known.GetHelpText());

        // The square's rectangle is its own, not the whole map's.
        Rect box = known.GetBoundingRectangle();
        Assert.True(box.Width > 0d && box.Height > 0d, $"The square reported no area: {box}.");
        Assert.True(box.Width < 40d, $"The square reported the whole map's area: {box}.");

        Assert.Contains(squares, square => square.GetName() == $"Combined, {today}, 118 tokens");
        Assert.Contains(
            squares,
            square => square.GetName()
                == $"Claude Code, {Today.AddDays(-364).ToString("D", CultureInfo.CurrentCulture)}, no data");
    }

    /// <summary>
    /// Hovering a square shows that day's tooltip, and moving onto a different one changes it.
    /// The grid is drawn rather than built, so nothing arrives on hover unless the map asks
    /// itself which square the pointer is over.
    /// </summary>
    [AvaloniaFact]
    public async Task HoveringASquareShowsThatDaysTooltip()
    {
        var history = new FakeHistoryService();
        history.AddDays(
            Day(ClaudeId, Today, input: 10, output: 5, cacheRead: 100, cacheWrite: 3),
            Day(ClaudeId, Today.AddDays(-1), input: 1, output: null, cacheRead: null, cacheWrite: null));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        UsageMapViewModel model = Map(history, claude);
        await model.LoadAsync(Ct);

        var map = new UsageMap { Rows = model.Rows, Scale = model.Scale };
        using PixelHost host = PixelHost.Show(map, ThemeVariant.Light, width: 900d, height: 260d);

        Assert.Null(ToolTip.GetTip(map));

        Point origin = host.BoundsOf(map).TopLeft;
        host.Window.MouseMove(Centre(map, origin, row: 0, Today), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(CellFor(model.Rows![0], Today).Detail, ToolTip.GetTip(map));

        host.Window.MouseMove(Centre(map, origin, row: 0, Today.AddDays(-1)), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(CellFor(model.Rows[0], Today.AddDays(-1)).Detail, ToolTip.GetTip(map));
    }

    /// <summary>
    /// On screen, the History page carries the map above the tape, and the map is fed the one
    /// scale the whole page is ranked on rather than being left to rank itself.
    /// </summary>
    [AvaloniaFact]
    public async Task TheHistoryPageShowsTheMapAboveTheTape()
    {
        var history = new FakeHistoryService();
        history.AddDays(Day(ClaudeId, Today));

        using ProviderViewModel claude = Provider(ClaudeId, ClaudeName);
        var page = new HistoryViewModel([claude], history, new TestClock(Readings.Now));
        await page.LoadAsync(Ct);

        var view = new HistoryView { DataContext = page };

        Surface.Show(view, window =>
        {
            UsageMap map = Assert.Single(Surface.Visible<UsageMap>(window));
            UsageTape tape = Assert.Single(Surface.Visible<UsageTape>(window));

            Assert.Same(page.Map.Rows, map.Rows);
            Assert.Same(page.Map.Scale, map.Scale);
            Assert.Equal(UsageFormat.HistoryEmpty, map.EmptyText);

            Point mapTop = Assert.IsType<Point>(map.TranslatePoint(default, window));
            Point tapeTop = Assert.IsType<Point>(tape.TranslatePoint(default, window));
            Assert.True(mapTop.Y < tapeTop.Y, $"The map is not above the tape: {mapTop.Y} vs {tapeTop.Y}.");
        }, width: 900d, height: 760d);
    }

    /// <summary>The centre of one square, in window coordinates.</summary>
    private static Point Centre(UsageMap map, Point origin, int row, DateOnly day)
    {
        Rect? cell = map.CellBounds(row, day);
        Assert.True(cell is not null, $"The map has no cell for {day:yyyy-MM-dd} in row {row}.");
        return cell!.Value.Center + new Point(origin.X, origin.Y);
    }

    private static string[] Lines(UsageMapCell cell)
    {
        Assert.False(string.IsNullOrWhiteSpace(cell.Detail), $"{cell.Day:yyyy-MM-dd} carries no tooltip.");
        return cell.Detail!.Split('\n');
    }

    private static UsageMapCell CellFor(UsageMapRow row, DateOnly day) =>
        Assert.Single(row.Cells, cell => cell.Day == day);

    private static ProviderViewModel Provider(string id, string displayName) =>
        new(new FakeUsageProvider(id, displayName), new TestClock(Readings.Now), AltimSettings.Default);

    private static UsageMapViewModel Map(FakeHistoryService history, params ProviderViewModel[] providers) =>
        new(providers, history, new TestClock(Readings.Now));

    private static UsageDay Tokens(string providerId, DateOnly day, long tokens) =>
        new(providerId, day, new TokenTotals(tokens, null, null, null), null,
            UsageDaySource.Observed, DateTimeOffset.UnixEpoch);

    private static UsageDay Day(
        string providerId,
        DateOnly day,
        long? input = 10,
        long? output = 5,
        long? cacheRead = 100,
        long? cacheWrite = 3,
        double? peak = 40d,
        UsageDaySource source = UsageDaySource.Observed) =>
        new(providerId, day, new TokenTotals(input, output, cacheRead, cacheWrite), peak, source,
            DateTimeOffset.UnixEpoch);
}
