using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;
using Shape = Avalonia.Controls.Shapes.Path;

namespace Altim.UI.Tests;

/// <summary>
/// The assertions that read the screen for the shapes the design reference introduced: the
/// card grid, the card footer's divider, the drop down pill, the activity chip, and the tray
/// panel's header and compact provider line.
/// </summary>
/// <remarks>
/// <para>
/// Same reasoning as <see cref="PixelTests"/>: each of these is a promise about pixels that a
/// property assertion cannot keep. A grid can report two columns while drawing them on top of
/// one another; a hairline divider can exist in the tree with no width; a chip can carry the
/// muted ground and be drawn on the muted ground; and the brand mark is a bitmap used as an
/// opacity mask over a brush, which is the single most likely thing in the interface to be
/// present in the tree, correct in every property, and invisible on screen.
/// </para>
/// <para>
/// Every captured frame is copied out and the platform bitmap disposed before the assertion
/// runs - see <see cref="Frame"/> - because an undisposed frame takes the renderer down.
/// </para>
/// </remarks>
public sealed class ReferencePixelTests
{
    private const double Gap = 24d;

    /// <summary>
    /// Wide enough for two cards, the grid draws two: one run of ink, a gap of exactly the
    /// column gap, then a second run of the same width.
    /// </summary>
    [AvaloniaFact]
    public void TheCardGridDrawsTwoColumnsWhenThereIsRoomForThem()
    {
        CardGrid grid = Grid(2);
        using PixelHost host = PixelHost.Show(grid, width: 800d, height: 120d);

        Frame frame = host.Capture();
        Assert.Equal(2, grid.Columns);

        // (800 - 24) / 2 = 388 per column.
        const int column = 388;
        Assert.Equal(column, frame.LongestRun(20, Ink.IsInk));
        Assert.Equal((column * 2) + (int)Gap, frame.SpanAt(20, Ink.IsInk));

        // The gap is page, not card: nothing is drawn in it.
        var between = new Rect(column, 0d, Gap, 40d);
        Assert.Equal(0, frame.Count(between, Ink.IsInk));

        // One row, so nothing is drawn below the cards.
        Assert.Equal(0, frame.Count(new Rect(0d, 48d, 800d, 60d), Ink.IsInk));
    }

    /// <summary>
    /// Too narrow for two cards of a readable width, the grid draws one column and stacks
    /// the cards instead of squeezing them.
    /// </summary>
    [AvaloniaFact]
    public void TheCardGridDropsToOneColumnWhenItIsNarrow()
    {
        CardGrid grid = Grid(2);
        using PixelHost host = PixelHost.Show(grid, width: 600d, height: 160d);

        Frame frame = host.Capture();

        // (600 - 24) / 2 = 288, under the 320 a card stops being readable at.
        Assert.Equal(1, grid.Columns);
        Assert.Equal(600, frame.LongestRun(20, Ink.IsInk));

        // The second card is a row down, one row gap below the first.
        Assert.Equal(0, frame.Count(new Rect(0d, 44d, 600d, 20d), Ink.IsInk));
        Assert.Equal(600, frame.LongestRun(84, Ink.IsInk));
    }

    /// <summary>
    /// The card footer is split by a vertical hairline: one device pixel wide, in the border
    /// colour, and taller than the two figures it stands between.
    /// </summary>
    [AvaloniaFact]
    public void TheCardFooterIsSplitByAVerticalHairline()
    {
        using ProviderViewModel row = Row();
        var card = new ProviderCardView { DataContext = row };
        using PixelHost host = PixelHost.Show(card, width: 420d, height: 420d);

        Frame frame = host.Capture();

        Separator divider = host.Window.GetVisualDescendants()
            .OfType<Separator>()
            .Single(s => s.Bounds.Width <= 1d && s.Bounds.Height > 1d);

        Rect bounds = host.BoundsOf(divider);
        Assert.Equal(1d, bounds.Width, 6);
        Assert.True(bounds.Height >= 40d, $"The divider is only {bounds.Height} tall.");

        // Every pixel down its column is the border colour: a hairline that had been laid
        // out at a fractional position would be a partial blend of ground and border.
        var column = new Rect(bounds.X, bounds.Y + 2d, 1d, bounds.Height - 4d);
        Assert.Equal(
            (int)(bounds.Height - 4d),
            frame.Count(column, colour => Ink.Near(colour, Ink.Border, 6)));

        // It is a divider between two cells, not an underline: nothing on its own row
        // either side of it inside the footer.
        Assert.Equal(0, frame.Count(new Rect(bounds.X - 8d, bounds.Center.Y, 6d, 1d), Ink.IsInk));
    }

    /// <summary>
    /// The range picker is the reference's pill: 32 tall, a 1px border all the way round,
    /// and corners rounded enough that the outermost corner pixel is page rather than rule.
    /// </summary>
    [AvaloniaFact]
    public void TheRangeDropDownIsAPill()
    {
        var combo = new ComboBox
        {
            ItemsSource = new[] { "Last 24 hours", "Last 7 days", "Last 30 days" },
            SelectedIndex = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(20d),
            MinWidth = 132d,
        };

        using PixelHost host = PixelHost.Show(combo, width: 240d, height: 96d);
        Frame frame = host.Capture();

        Rect bounds = host.BoundsOf(combo);
        Assert.Equal(32d, bounds.Height, 6);

        // The top rule, clear of both corners.
        var top = new Rect(bounds.X + 12d, bounds.Y, bounds.Width - 24d, 1d);
        Assert.True(
            frame.Count(top, colour => Ink.Near(colour, Ink.Border, 8)) >= (int)(bounds.Width - 24d) - 2,
            $"The pill has no top rule: {frame.Describe(top)}");

        // The corner is cut by the radius, so the rule does not reach it.
        Assert.True(
            Ink.Near(frame.At(new Point(bounds.X, bounds.Y)), Ink.Surface, 4),
            $"The pill's corner is not rounded: {frame.At(new Point(bounds.X, bounds.Y))}");
    }

    /// <summary>
    /// The activity chip is a muted ground under a provider's own identifier, with corners
    /// rounded off it. On the page's own ground the two differ by five levels, which is the
    /// whole point: the chip is quiet, not invisible.
    /// </summary>
    [AvaloniaFact]
    public void TheActivityChipCarriesAMutedGround()
    {
        var chip = new Border
        {
            Theme = PixelHost.Theme("AltimChip"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(20d),
            Child = new TextBlock
            {
                Theme = PixelHost.Theme("AltimChipText"),
                Text = "claude-opus-4",
            },
        };

        using PixelHost host = PixelHost.Show(chip, width: 240d, height: 96d);
        Frame frame = host.Capture();

        Rect bounds = host.BoundsOf(chip);

        // The ground reaches the chip's own edge, one row inside its top.
        var band = new Rect(bounds.X + 8d, bounds.Y + 1d, bounds.Width - 16d, 1d);
        Assert.True(
            frame.Count(band, colour => Ink.Near(colour, Ink.SurfaceMuted)) > 0,
            $"The chip has no muted ground: {frame.Describe(band)}");

        // Radius 4: the outermost corner pixel is page.
        Assert.True(
            Ink.Near(frame.At(new Point(bounds.X, bounds.Y)), Ink.Surface, 2),
            $"The chip's corner is not rounded: {frame.At(new Point(bounds.X, bounds.Y))}");

        // And the identifier is set in it.
        Assert.True(
            frame.Count(bounds, Ink.IsInk) > 0
                || frame.Count(bounds, colour => Ink.Near(colour, Ink.TextSecondary, 40)) > 0,
            $"The chip carries no text: {frame.Describe(bounds)}");
    }

    /// <summary>
    /// The panel's provider row is one line, and it carries no meter. A meter reports a
    /// level by its width, so a meter at this width is a rail of track colour a hundred
    /// pixels long - which is exactly what this looks for the absence of.
    /// </summary>
    [AvaloniaFact]
    public void ThePanelProviderLineIsOneLineWithNoMeter()
    {
        using PopupViewModel panel = Panel();
        var view = new PopupView { DataContext = panel };
        // Tall enough to carry the dial heading the panel as well as the provider rows.
        using PixelHost host = PixelHost.Show(view, width: 320d, height: 640d);

        Frame frame = host.Capture();

        Assert.Empty(host.Window.GetVisualDescendants().OfType<Meter>());

        // Scoped to the provider rows. The panel's own section rules are border colour too,
        // and full bleed, so a scan of the whole panel would report every rule as a meter -
        // and the dial heading the panel carries the same figure as the row below it, so an
        // unscoped search for the text finds that as well.
        ItemsControl rows = host.Window.GetVisualDescendants()
            .OfType<ItemsControl>()
            .First(c => ReferenceEquals(c.ItemsSource, panel.Providers));

        // Every figure on the row shares one baseline: the compact line is one line.
        IReadOnlyList<TextBlock> figures = rows.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.Text is "62%" or "38%")
            .ToList();

        Assert.Equal(2, figures.Count);
        Assert.Equal(
            host.BoundsOf(figures[0]).Center.Y,
            host.BoundsOf(figures[1]).Center.Y,
            1);

        Rect area = host.BoundsOf(rows);
        Assert.True(area.Height < 72d, $"The provider row is {area.Height} tall, not one line.");

        for (int y = (int)area.Y; y < (int)area.Bottom; y++)
        {
            Assert.True(
                frame.LongestRun(y, colour => Ink.Near(colour, Ink.Border, 2)) < 100,
                $"Row {y} carries a meter rail: {frame.Describe(new Rect(area.X, y, area.Width, 1d))}");
        }
    }

    /// <summary>
    /// The panel's header paints the Altim mark, the wordmark and the gear, on one line.
    /// </summary>
    /// <remarks>
    /// The mark is the template bitmap drawn as an opacity mask over TextPrimary. Every
    /// property of that arrangement can be right while nothing is painted - a mask that did
    /// not load leaves the brush showing through a mask of zero - so the assertion that
    /// matters is that ink landed inside the mark's own 22px box.
    /// </remarks>
    [AvaloniaFact]
    public void ThePanelHeaderPaintsTheMarkTheWordmarkAndTheGear()
    {
        using PopupViewModel panel = Panel();
        var view = new PopupView { DataContext = panel };
        using PixelHost host = PixelHost.Show(view, width: 320d, height: 420d);

        Frame frame = host.Capture();

        Border mark = host.Window.GetVisualDescendants()
            .OfType<Border>()
            .First(b => b.Theme == PixelHost.Theme("AltimBrandMark"));

        Rect bounds = host.BoundsOf(mark);
        Assert.Equal(22d, bounds.Width, 6);
        Assert.Equal(22d, bounds.Height, 6);

        int painted = frame.Count(bounds, Ink.IsInk);
        Assert.True(
            painted > 40,
            $"The Altim mark painted {painted} pixels: {frame.Describe(bounds)}");

        // The wordmark sits beside it on the same line.
        TextBlock wordmark = host.Window.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(t => t.Text == "Altim");

        Rect word = host.BoundsOf(wordmark);
        Assert.True(word.X > bounds.Right, "The wordmark is not to the right of the mark.");
        Assert.True(
            Math.Abs(word.Center.Y - bounds.Center.Y) < 4d,
            "The wordmark and the mark are not on one line.");

        // The gear is at the far end of the same row, and it is drawn.
        Shape gear = host.Window.GetVisualDescendants()
            .OfType<Shape>()
            .First(p => p.Classes.Contains("gear"));

        Rect gearBounds = host.BoundsOf(gear);
        Assert.True(gearBounds.X > 240d, $"The gear is at {gearBounds.X}, not at the far end.");
        Assert.True(
            frame.Count(gearBounds, colour => !Ink.Near(colour, Ink.SurfaceMuted, 6)) > 8,
            $"The gear painted nothing: {frame.Describe(gearBounds)}");
    }

    private static CardGrid Grid(int children)
    {
        var grid = new CardGrid
        {
            MinimumColumnWidth = 320d,
            ColumnGap = Gap,
            RowGap = Gap,
            VerticalAlignment = VerticalAlignment.Top,
        };

        for (int i = 0; i < children; i++)
        {
            grid.Children.Add(new Border
            {
                Height = 40d,
                Background = new SolidColorBrush(Ink.TextPrimary),
            });
        }

        return grid;
    }

    private static ProviderViewModel Row()
    {
        var provider = new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude"));
        var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);
        row.Apply(provider.Reading);
        return row;
    }

    private static PopupViewModel Panel()
    {
        var provider = new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude"));
        var panel = new PopupViewModel(
            [provider],
            new TestClock(Readings.Now),
            AltimSettings.Default);

        panel.Providers[0].Apply(provider.Reading);
        return panel;
    }
}
