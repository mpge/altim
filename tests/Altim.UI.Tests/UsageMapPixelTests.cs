using Altim.UI.Controls;
using Altim.UI.History;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The map's one uncompromisable rule, read off the screen: a day nothing is known about
/// and a day known to be empty are different squares.
/// </summary>
/// <remarks>
/// <para>
/// The distinction is the product's whole position on honesty. A grid that paints an
/// unknown day as a zero day is the application telling the user it watched a day it did
/// not - before install, outside a provider's backfill reach, or a provider that was never
/// there. Every other rendering detail in the map may be traded for tidiness; this one may
/// not, so it is asserted in pixels rather than in properties, and in both theme variants,
/// because the ramp and the outline are different tokens and a variant can lose one of them
/// on its own.
/// </para>
/// <para>
/// Every assertion here was run against a deliberately wrong control - one that painted an
/// unknown cell with the level zero fill - and every one of them failed. An assertion that
/// survives that implementation is not testing anything.
/// </para>
/// <para>
/// Frames are captured through <see cref="PixelHost"/>, which copies the pixels out and
/// disposes the platform bitmap before returning: an undisposed frame takes the renderer
/// down, and this suite has its own memory budget.
/// </para>
/// </remarks>
public sealed class UsageMapPixelTests
{
    private static readonly DateOnly Day = new(2026, 9, 14);

    /// <summary>Both variants, so a ramp that only exists in Light fails here.</summary>
    public static TheoryData<string> Variants => ["Light", "Dark"];

    /// <summary>
    /// An unknown day is a hairline outline standing on the ground it was drawn on. Not a
    /// fill, and not nothing: a cell that painted nothing at all would also leave the
    /// ground showing, so the edge is asserted as well as the middle.
    /// </summary>
    /// <param name="variant">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AnUnknownDayIsAnOutlineWithNoFill(string variant)
    {
        ThemeVariant theme = Variant(variant);
        var map = new UsageMap { Rows = [Row("Claude", Unknown(Day), Known(Day.AddDays(1), 5_000))] };

        using PixelHost host = PixelHost.Show(map, theme, width: 200d, height: 120d);
        Frame frame = host.Capture();

        Rect cell = Cell(host, map, row: 0, Day);
        Color ground = Token(theme, "AltimSurfaceBrush");
        Color outline = Token(theme, "AltimBorderBrush");

        Rect middle = Inside(cell);
        Assert.True(
            frame.Count(middle, colour => !Ink.Near(colour, ground, 2)) == 0,
            $"An unknown day was filled rather than left open: {frame.Describe(middle)}");

        Rect top = new(cell.X + 2d, cell.Y, Math.Max(1d, cell.Width - 4d), 1d);
        Assert.True(
            frame.Count(top, colour => Ink.Near(colour, outline, 3)) >= (int)top.Width,
            $"An unknown day drew no hairline outline: {frame.Describe(top)}");
    }

    /// <summary>
    /// A day known to have used nothing is the faintest step of the ramp, and the ramp is
    /// <c>TextPrimary</c> at an opacity rather than the border colour: a control that filled
    /// zero days in <c>Border</c> would be drawing the outline's colour as data.
    /// </summary>
    /// <param name="variant">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AZeroDayIsTheFaintestFillInTheRamp(string variant)
    {
        ThemeVariant theme = Variant(variant);
        var map = new UsageMap { Rows = [Row("Claude", Known(Day, 0), Known(Day.AddDays(1), 5_000))] };

        using PixelHost host = PixelHost.Show(map, theme, width: 200d, height: 120d);
        Frame frame = host.Capture();

        Rect middle = Inside(Cell(host, map, row: 0, Day));
        Color ground = Token(theme, "AltimSurfaceBrush");
        Color faintest = Over(theme, "AltimMapLevel0Brush", ground);

        Assert.True(
            frame.Count(middle, colour => Ink.Near(colour, ground, 2)) == 0,
            $"A zero day was left unfilled: {frame.Describe(middle)}");
        Assert.True(
            frame.Count(middle, colour => Ink.Near(colour, faintest, 3)) == (int)(middle.Width * middle.Height),
            $"A zero day is not the ramp's faintest step ({faintest}): {frame.Describe(middle)}");
    }

    /// <summary>
    /// The two squares, side by side in one map, are not the same pixels. This is the
    /// assertion the spec names, and it is the one a control that treats a missing day as a
    /// zero cannot pass however tidy the result looks.
    /// </summary>
    /// <param name="variant">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void UnknownAndZeroAreNotTheSameSquare(string variant)
    {
        ThemeVariant theme = Variant(variant);
        var map = new UsageMap
        {
            Rows = [Row("Claude", Unknown(Day), Known(Day.AddDays(1), 0), Known(Day.AddDays(2), 5_000))],
        };

        using PixelHost host = PixelHost.Show(map, theme, width: 200d, height: 120d);
        Frame frame = host.Capture();

        Rect unknown = Cell(host, map, row: 0, Day);
        Rect zero = Cell(host, map, row: 0, Day.AddDays(1));

        Assert.Equal(unknown.Width, zero.Width, 6);
        Assert.Equal(unknown.Height, zero.Height, 6);

        int different = 0;
        for (int y = 0; y < (int)unknown.Height; y++)
        {
            for (int x = 0; x < (int)unknown.Width; x++)
            {
                Color left = frame.At((int)unknown.X + x, (int)unknown.Y + y);
                Color right = frame.At((int)zero.X + x, (int)zero.Y + y);
                different += left == right ? 0 : 1;
            }
        }

        Assert.True(
            different >= (int)(unknown.Width * unknown.Height) / 2,
            $"Unknown and zero differ in only {different} pixels. Unknown: "
                + $"{frame.Describe(unknown)}. Zero: {frame.Describe(zero)}.");
    }

    /// <summary>
    /// The ramp carries the value. A day at the top of the scale stands further off the
    /// ground than a day at the bottom, which is "darker" in Light and "lighter" in Dark,
    /// so the assertion is distance from the ground rather than luminance: a control whose
    /// ramp is five copies of one brush fails here in both variants.
    /// </summary>
    /// <param name="variant">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AHeavyDayCarriesMoreInkThanAQuietOne(string variant)
    {
        ThemeVariant theme = Variant(variant);

        // Ten ranked days, so the quantile scale puts the first on level 0 and the last on
        // level 4. The control is handed no scale, so it ranks what it was given.
        UsageMapCell[] cells = new UsageMapCell[10];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = Known(Day.AddDays(i), (i + 1) * 1_000);
        }

        var map = new UsageMap { Rows = [new UsageMapRow("Claude", cells)] };

        using PixelHost host = PixelHost.Show(map, theme, width: 260d, height: 140d);
        Frame frame = host.Capture();

        Color ground = Token(theme, "AltimSurfaceBrush");
        double quiet = Distance(frame, Inside(Cell(host, map, row: 0, Day)), ground);
        double heavy = Distance(frame, Inside(Cell(host, map, row: 0, Day.AddDays(9))), ground);

        Assert.True(
            heavy > quiet + 40d,
            $"The ramp does not separate a heavy day ({heavy:F1}) from a quiet one ({quiet:F1}).");
    }

    /// <summary>
    /// A cell marked known is a fill even when it carries no token figure: the flag decides,
    /// not the number.
    /// </summary>
    /// <remarks>
    /// The two are separate so that a day known to have used nothing — a per-day source that
    /// reported it and found nothing there — is the ramp's foot rather than an outline.
    /// Drawing that as an outline would be the same lie as drawing an unknown day as a zero,
    /// told the other way round. The scale itself answers unknown for a null, so the control
    /// has to honour the flag rather than pass the null through. Which days are marked known
    /// is the view model's decision, not this control's.
    /// </remarks>
    [AvaloniaFact]
    public void AKnownDayWithNoFigureIsAFillRatherThanAnOutline()
    {
        var map = new UsageMap
        {
            Rows =
            [
                Row(
                    "Claude",
                    new UsageMapCell(Day, Tokens: null, PeakPercent: null, IsKnown: true),
                    Known(Day.AddDays(1), 5_000)),
            ],
        };

        using PixelHost host = PixelHost.Show(map, ThemeVariant.Light, width: 200d, height: 120d);
        Frame frame = host.Capture();

        Rect middle = Inside(Cell(host, map, row: 0, Day));
        Color ground = Token(ThemeVariant.Light, "AltimSurfaceBrush");
        Color faintest = Over(ThemeVariant.Light, "AltimMapLevel0Brush", ground);

        Assert.True(
            frame.Count(middle, colour => Ink.Near(colour, faintest, 3)) == (int)(middle.Width * middle.Height),
            $"A day with a row but no figure was not filled at the ramp's foot: {frame.Describe(middle)}");
    }

    /// <summary>
    /// A provider with nothing in the range keeps its row, drawn as outlines, while another
    /// provider has data. That is the point of the outline: the left edge of one row being
    /// older than another's is a fact about backfill reach, and filling the difference with
    /// zeroes would hide it.
    /// </summary>
    [AvaloniaFact]
    public void AProviderWithNoKnownDaysKeepsARowOfOutlines()
    {
        var map = new UsageMap
        {
            Rows =
            [
                Row("Claude", Known(Day, 1_000), Known(Day.AddDays(1), 9_000)),
                Row("Codex", Unknown(Day), Unknown(Day.AddDays(1))),
            ],
        };

        using PixelHost host = PixelHost.Show(map, ThemeVariant.Light, width: 200d, height: 160d);
        Frame frame = host.Capture();

        Color ground = Token(ThemeVariant.Light, "AltimSurfaceBrush");
        Color outline = Token(ThemeVariant.Light, "AltimBorderBrush");

        Rect cell = Cell(host, map, row: 1, Day);
        Rect middle = Inside(cell);

        Assert.True(
            frame.Count(middle, colour => !Ink.Near(colour, ground, 2)) == 0,
            $"The empty provider's day was filled: {frame.Describe(middle)}");

        Rect top = new(cell.X + 2d, cell.Y, Math.Max(1d, cell.Width - 4d), 1d);
        Assert.True(
            frame.Count(top, colour => Ink.Near(colour, outline, 3)) >= (int)top.Width,
            $"The empty provider's row was dropped rather than outlined: {frame.Describe(top)}");
    }

    /// <summary>
    /// A row carrying no cells contributes nothing and does not take the map down with it.
    /// The rows around it are unaffected, which is what proves it was skipped rather than
    /// drawn as a strip of nothing.
    /// </summary>
    [AvaloniaFact]
    public void ARowWithNoCellsRendersNothing()
    {
        var withEmpty = new UsageMap
        {
            Rows =
            [
                new UsageMapRow("Nothing", []),
                Row("Claude", Known(Day, 1_000), Known(Day.AddDays(1), 9_000)),
            ],
        };

        var withoutEmpty = new UsageMap
        {
            Rows = [Row("Claude", Known(Day, 1_000), Known(Day.AddDays(1), 9_000))],
        };

        Frame? withEmptyFrame = null;
        Rect area = default;

        using (PixelHost host = PixelHost.Show(withEmpty, ThemeVariant.Light, width: 200d, height: 140d))
        {
            withEmptyFrame = host.Capture();
            area = host.BoundsOf(withEmpty);

            Assert.Null(withEmpty.CellBounds(0, Day));
            Assert.NotNull(withEmpty.CellBounds(1, Day));
        }

        using (PixelHost host = PixelHost.Show(withoutEmpty, ThemeVariant.Light, width: 200d, height: 140d))
        {
            Frame frame = host.Capture();

            // The row that carried nothing took no room and left no mark: the map with it
            // is the same picture, to the pixel, as the map without it.
            Assert.Equal(withoutEmpty.CellBounds(0, Day), withEmpty.CellBounds(1, Day));
            Assert.Equal(host.BoundsOf(withoutEmpty), area);
            Assert.Equal(0, frame.DifferenceWith(withEmptyFrame, area));
        }
    }

    /// <summary>
    /// No rows at all is a sentence, not a grid of empty squares pretending to be a map.
    /// A map that has not been given anything yet is neither: it says nothing, because it
    /// has not been told anything and announcing an empty history it was never handed would
    /// be the same lie as painting unknown days as zero.
    /// </summary>
    [AvaloniaFact]
    public void NoRowsIsASentenceAndUnsetRowsIsSilence()
    {
        var unset = new UsageMap();
        using (PixelHost host = PixelHost.Show(unset, ThemeVariant.Light, width: 240d, height: 120d))
        {
            Frame frame = host.Capture();
            Rect area = host.BoundsOf(unset);
            Color ground = Token(ThemeVariant.Light, "AltimSurfaceBrush");

            Assert.True(
                frame.Count(area, colour => !Ink.Near(colour, ground, 2)) == 0,
                $"A map that was given no rows drew something: {frame.Describe(area)}");
        }

        var empty = new UsageMap { Rows = [] };
        using (PixelHost host = PixelHost.Show(empty, ThemeVariant.Light, width: 240d, height: 120d))
        {
            Frame frame = host.Capture();
            Rect area = host.BoundsOf(empty);
            Color ground = Token(ThemeVariant.Light, "AltimSurfaceBrush");

            Assert.True(
                frame.Count(area, colour => !Ink.Near(colour, ground, 2)) > 0,
                "A map with no days at all drew neither a sentence nor a grid.");
        }

        var noKnownDays = new UsageMap { Rows = [Row("Claude", Unknown(Day), Unknown(Day.AddDays(1)))] };
        using (PixelHost host = PixelHost.Show(noKnownDays, ThemeVariant.Light, width: 240d, height: 120d))
        {
            Frame frame = host.Capture();
            Rect area = host.BoundsOf(noKnownDays);
            Color ground = Token(ThemeVariant.Light, "AltimSurfaceBrush");

            // Not one provider has a single day of data, so there is no map to draw and no
            // grid of empty squares pretending to be one. There is a sentence.
            Assert.Null(noKnownDays.CellBounds(0, Day));
            Assert.True(
                frame.Count(area, colour => !Ink.Near(colour, ground, 2)) > 0,
                "A map with no known day drew neither a sentence nor a grid.");
        }
    }

    /// <summary>
    /// The ramp is five ascending steps of <c>TextPrimary</c> opacity in every variant, and
    /// nothing else. Provider accent is identity and never data, so a ramp that reached for
    /// one would be tinting a token's meaning; a ramp that repeated a step would be a scale
    /// with a level nobody can see.
    /// </summary>
    /// <param name="variant">The theme variant to check.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheRampIsFiveAscendingStepsOfTextPrimary(string variant)
    {
        ThemeVariant theme = Variant(variant);
        Color ink = Token(theme, "AltimTextPrimaryBrush");

        double previous = 0d;
        for (int level = 0; level < 5; level++)
        {
            ISolidColorBrush brush = Brush(theme, $"AltimMapLevel{level}Brush");

            Assert.Equal(ink, brush.Color);
            Assert.True(
                brush.Opacity > previous,
                $"AltimMapLevel{level}Brush is not above the step below it.");
            Assert.InRange(brush.Opacity, 0d, 1d);
            previous = brush.Opacity;
        }
    }

    private static UsageMapRow Row(string name, params UsageMapCell[] cells) => new(name, cells);

    private static UsageMapCell Known(DateOnly day, long tokens) => new(day, tokens, null, IsKnown: true);

    private static UsageMapCell Unknown(DateOnly day) => new(day, null, null, IsKnown: false);

    private static ThemeVariant Variant(string name) =>
        name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>One cell's rectangle in window coordinates.</summary>
    private static Rect Cell(PixelHost host, UsageMap map, int row, DateOnly day)
    {
        Rect? local = map.CellBounds(row, day);
        Assert.True(local is not null, $"The map has no cell for {day:yyyy-MM-dd} in row {row}.");

        Rect origin = host.BoundsOf(map);
        return local!.Value.Translate(new Vector(origin.X, origin.Y));
    }

    /// <summary>
    /// The middle of a cell, clear of its own edge on every side. The edge is where an
    /// outline lives and where a fill is antialiased against the ground, so an assertion
    /// about what a square is made of has to read the part of it that is only ever one
    /// thing.
    /// </summary>
    private static Rect Inside(Rect cell) =>
        new(cell.X + 2d, cell.Y + 2d, Math.Max(1d, cell.Width - 4d), Math.Max(1d, cell.Height - 4d));

    /// <summary>The mean per channel distance between an area and a colour.</summary>
    private static double Distance(Frame frame, Rect area, Color from)
    {
        double total = 0d;
        int count = 0;

        for (int y = (int)area.Y; y < (int)area.Bottom; y++)
        {
            for (int x = (int)area.X; x < (int)area.Right; x++)
            {
                Color colour = frame.At(x, y);
                total += (Math.Abs(colour.R - from.R) + Math.Abs(colour.G - from.G)
                          + Math.Abs(colour.B - from.B)) / 3d;
                count++;
            }
        }

        return count == 0 ? 0d : total / count;
    }

    /// <summary>A ramp brush composited over the ground it is drawn on.</summary>
    private static Color Over(ThemeVariant variant, string key, Color ground)
    {
        ISolidColorBrush brush = Brush(variant, key);
        double alpha = brush.Opacity * (brush.Color.A / 255d);

        byte Mix(byte over, byte under) => (byte)Math.Round((over * alpha) + (under * (1d - alpha)));

        return Color.FromArgb(
            0xFF,
            Mix(brush.Color.R, ground.R),
            Mix(brush.Color.G, ground.G),
            Mix(brush.Color.B, ground.B));
    }

    private static Color Token(ThemeVariant variant, string key) => Brush(variant, key).Color;

    private static ISolidColorBrush Brush(ThemeVariant variant, string key)
    {
        Assert.True(
            DesignSystem.Ensure().TryGetResource(key, variant, out object? value),
            $"{key} does not resolve under {variant}.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value);
    }
}
