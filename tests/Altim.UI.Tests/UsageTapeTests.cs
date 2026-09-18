using Altim.UI.Controls;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The tape's contract is mostly about the shapes of history it must survive: nothing
/// recorded yet, exactly one sample, a provider that stopped reporting halfway through,
/// and a run long enough that per sample dots would turn into texture.
/// </summary>
public sealed class UsageTapeTests
{
    /// <summary>The empty sentence is the one in DESIGN.md, verbatim.</summary>
    [AvaloniaFact]
    public void EmptyTextIsTheSentenceFromTheBrief() =>
        Assert.Equal(
            "No usage recorded yet. Altim starts collecting when an agent runs.",
            new UsageTape().EmptyText);

    /// <summary>Rules are drawn at 25, 50, 75 and 100. There is no rule at zero.</summary>
    [AvaloniaFact]
    public void LevelsAreNoneHalfAndFull() =>
        Assert.Equal<double>([0d, 50d, 100d], UsageTape.Levels);

    /// <summary>Dots stop at 32 points.</summary>
    [AvaloniaFact]
    public void DotSampleLimitIsThirtyTwo() => Assert.Equal(32, UsageTape.DotSampleLimit);

    /// <summary>Zero is the bottom of the plot and 100 is the top.</summary>
    /// <param name="level">The level.</param>
    /// <param name="height">The plot height.</param>
    /// <param name="expected">The expected offset from the top.</param>
    [Theory]
    [InlineData(0d, 100d, 100d)]
    [InlineData(25d, 100d, 75d)]
    [InlineData(50d, 100d, 50d)]
    [InlineData(100d, 100d, 0d)]
    [InlineData(150d, 100d, 0d)]
    [InlineData(-10d, 100d, 100d)]
    public void LevelsMapFromTheBottomUp(double level, double height, double expected) =>
        Assert.Equal(expected, UsageTape.YFor(level, height), 6);

    /// <summary>
    /// The newest sample sits on the right edge, which is also where a single sample goes.
    /// </summary>
    /// <param name="index">The sample index.</param>
    /// <param name="count">The sample count.</param>
    /// <param name="width">The plot width.</param>
    /// <param name="expected">The expected offset from the left.</param>
    [Theory]
    [InlineData(0, 1, 200d, 200d)]
    [InlineData(0, 2, 200d, 0d)]
    [InlineData(1, 2, 200d, 200d)]
    [InlineData(0, 5, 200d, 0d)]
    [InlineData(2, 5, 200d, 100d)]
    [InlineData(4, 5, 200d, 200d)]
    [InlineData(0, 0, 200d, 200d)]
    [InlineData(0, 4, 0d, 0d)]
    public void SamplesRunLeftToRightEndingAtTheRightEdge(
        int index,
        int count,
        double width,
        double expected) =>
        Assert.Equal(expected, UsageTape.XFor(index, count, width), 6);

    /// <summary>A tape with no series draws the empty state instead of an empty plot.</summary>
    [AvaloniaFact]
    public void RendersWithNoSeries() => DrawsSomething(() => new UsageTape());

    /// <summary>An empty series list draws the same picture as no series.</summary>
    [AvaloniaFact]
    public void RendersWithAnEmptySeriesList() =>
        DrawsTheEmptyState(() => new UsageTape { Series = [] });

    /// <summary>A series with no samples in it draws the same picture as no series.</summary>
    [AvaloniaFact]
    public void RendersWithASeriesThatHasNoSamples() =>
        DrawsTheEmptyState(() => new UsageTape { Series = [new UsageTapeSeries("Claude", [])] });

    /// <summary>
    /// A series where every sample is unreported draws nothing rather than a flat line at
    /// zero. A null is a null, so the picture is the empty state's, to the pixel.
    /// </summary>
    [AvaloniaFact]
    public void RendersWithASeriesOfOnlyUnreportedSamples() =>
        DrawsTheEmptyState(
            () => new UsageTape { Series = [new UsageTapeSeries("Claude", [null, null, null])] });

    /// <summary>One sample is a picture, not a crash and not the empty sentence.</summary>
    [AvaloniaFact]
    public void RendersASinglePointSeries() =>
        DrawsAPlot(() => new UsageTape { Series = [new UsageTapeSeries("Claude", [42d])] });

    /// <summary>Two samples are the shortest thing that is actually a line.</summary>
    [AvaloniaFact]
    public void RendersATwoPointSeries() =>
        DrawsAPlot(() => new UsageTape { Series = [new UsageTapeSeries("Claude", [10d, 90d])] });

    /// <summary>A gap breaks the line rather than dropping it to zero.</summary>
    [AvaloniaFact]
    public void RendersASeriesWithGaps() =>
        DrawsAPlot(
            () => new UsageTape
            {
                Series = [new UsageTapeSeries("Claude", [10d, null, 40d, null, null, 80d])],
            });

    /// <summary>Sample counts on both sides of the dot limit draw a plot.</summary>
    /// <param name="count">The number of samples.</param>
    [AvaloniaTheory]
    [InlineData(3)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(256)]
    public void RendersAcrossTheDotLimit(int count) =>
        DrawsAPlot(() => new UsageTape { Series = [Ramp("Claude", count)] });

    /// <summary>Two providers, one primary and one secondary, named at the end of each line.</summary>
    [AvaloniaFact]
    public void RendersTwoSeries() =>
        DrawsAPlot(
            () => new UsageTape
            {
                Series =
                [
                    Ramp("Claude", 12),
                    new UsageTapeSeries("Codex", [80d, 70d, 55d, 55d, 30d], UsageTapeEmphasis.Secondary),
                ],
            });

    /// <summary>
    /// A tape squeezed to nothing does not throw, and draws nothing rather than a blot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one case here that may legitimately paint no pixels at all, which is why it takes
    /// <see cref="DesignSystem.AssertRendersNothing"/> rather than the blanket "it must paint
    /// ink" every other case takes. Two columns leave the plot a negative width, and a tape
    /// that drew a reading into that would be reporting a level nobody can read off it.
    /// </para>
    /// <para>
    /// The window is two wide and a tape tall, not two by two. The tape's theme sets a 96
    /// minimum height, so a two by two window does not squeeze the tape: it arranges a 2 by
    /// 96 tape centred on a 2 tall window, which leaves 47 of it above the top edge and 47
    /// below the bottom. Nothing could be read off that frame either way, and the assertion
    /// this replaced - that a 2 by 2 frame was more than 0 wide - could not tell the
    /// difference.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void RendersWithNoRoomToDrawIn() =>
        DesignSystem.AssertRendersNothing(
            new UsageTape { Series = [Ramp("Claude", 8)] },
            width: 2d,
            height: 140d);

    /// <summary>Level labels can be turned off without the plot losing its gutters.</summary>
    [AvaloniaFact]
    public void RendersWithoutLevelLabels() =>
        DrawsAPlot(
            () => new UsageTape { ShowLevelLabels = false, Series = [Ramp("Claude", 8)] });

    /// <summary>Out of range samples are clamped into the plot rather than drawn outside it.</summary>
    [AvaloniaFact]
    public void RendersOutOfRangeSamples() =>
        DrawsAPlot(
            () => new UsageTape { Series = [new UsageTapeSeries("Claude", [-40d, 240d, double.NaN, 50d])] });

    /// <summary>
    /// Two series ending at the same level get their names pushed apart rather than set on
    /// top of one another. The names are the legend - there is no legend box - so two of
    /// them on one row makes the tape unreadable exactly where it is busiest.
    /// </summary>
    [AvaloniaFact]
    public void LabelsAtTheSameLevelArePushedApart()
    {
        IReadOnlyList<double> placed = UsageTape.SpreadLabels([50d, 50d, 50d], 14d, 0d, 100d);

        Assert.Equal(3, placed.Count);
        Assert.Equal(50d, placed[0], 6);
        Assert.Equal(64d, placed[1], 6);
        Assert.Equal(78d, placed[2], 6);
    }

    /// <summary>A label that is already clear of its neighbours does not move.</summary>
    [AvaloniaFact]
    public void LabelsThatDoNotCollideStayWhereTheirLineEnds()
    {
        IReadOnlyList<double> placed = UsageTape.SpreadLabels([10d, 60d], 14d, 0d, 100d);

        Assert.Equal(10d, placed[0], 6);
        Assert.Equal(60d, placed[1], 6);
    }

    /// <summary>The result is in the order it was given, not in vertical order.</summary>
    [AvaloniaFact]
    public void LabelsComeBackInTheOrderTheyWereGiven()
    {
        IReadOnlyList<double> placed = UsageTape.SpreadLabels([80d, 10d, 82d], 14d, 0d, 100d);

        Assert.Equal(80d, placed[0], 6);
        Assert.Equal(10d, placed[1], 6);
        Assert.Equal(94d, placed[2], 6);
    }

    /// <summary>
    /// A stack that would run off the bottom is pulled back up, so a label is never drawn
    /// outside the control it belongs to.
    /// </summary>
    [AvaloniaFact]
    public void LabelsNearTheBottomArePulledBackInside()
    {
        IReadOnlyList<double> placed = UsageTape.SpreadLabels([96d, 98d, 99d], 14d, 0d, 100d);

        Assert.All(placed, top => Assert.InRange(top, 0d, 100d));
        Assert.Equal(72d, placed[0], 6);
        Assert.Equal(86d, placed[1], 6);
        Assert.Equal(100d, placed[2], 6);
    }

    /// <summary>Nothing to place is not an error.</summary>
    [AvaloniaFact]
    public void SpreadingNoLabelsIsEmpty() =>
        Assert.Empty(UsageTape.SpreadLabels([], 14d, 0d, 100d));

    /// <summary>
    /// Two providers ending at a similar level render, and the tape is the shape that
    /// makes their names collide: both lines finish at the right edge.
    /// </summary>
    [AvaloniaFact]
    public void RendersTwoSeriesEndingAtTheSameLevel() =>
        DrawsAPlot(
            () => new UsageTape
            {
                Series =
                [
                    new UsageTapeSeries("Claude Code", [10d, 30d, 62d]),
                    new UsageTapeSeries("Codex", [80d, 70d, 62d], UsageTapeEmphasis.Secondary),
                ],
            });

    /// <summary>
    /// The tape draws its own text, so the tabular figures the theme hands down have to
    /// reach it: evenly spaced rules need evenly spaced labels.
    /// </summary>
    [AvaloniaFact]
    public void TheThemeHandsTheTapeItsTabularFigures()
    {
        var tape = new UsageTape { Series = [new UsageTapeSeries("Claude", [20d, 60d])] };

        DesignSystem.AssertRenders(tape, width: 360d, height: 140d);

        FontFeatureCollection? features = TextElement.GetFontFeatures(tape);
        Assert.NotNull(features);
        Assert.Equal(2, features.Count);
        Assert.Equal("tnum", features[0].Tag);
        Assert.Equal("zero", features[1].Tag);
    }

    /// <summary>
    /// The empty sentence wraps inside the control. Laid out on one line it is wider than
    /// any panel in the product, so without a width it simply runs off the right edge,
    /// which reads as a truncated sentence rather than a wrapped one.
    /// </summary>
    [AvaloniaFact]
    public void TheEmptySentenceWrapsRatherThanRunningOffTheEdge()
    {
        var tape = new UsageTape();
        using PixelHost host = PixelHost.Show(tape, width: 200d, height: 120d);

        Frame frame = host.Capture();
        Rect bounds = host.BoundsOf(tape);

        // Ink on more than one line means it wrapped; ink in the last column would mean it
        // was still running when the control ran out.
        var body = new Rect(bounds.X, bounds.Y, bounds.Width - 1d, bounds.Height);
        var lastColumn = new Rect(bounds.Right - 1d, bounds.Y, 1d, bounds.Height);

        int rows = 0;
        for (int y = (int)bounds.Y; y < (int)bounds.Bottom; y++)
        {
            if (frame.Any(new Rect(body.X, y, body.Width, 1d), colour => !Ink.Near(colour, Ink.Surface, 24)))
            {
                rows++;
            }
        }

        Assert.True(rows > 14, $"The sentence occupies {rows} rows, so it did not wrap.");
        Assert.Equal(0, frame.Count(lastColumn, colour => !Ink.Near(colour, Ink.Surface, 24)));
    }

    private static UsageTapeSeries Ramp(string name, int count)
    {
        var values = new double?[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = count == 1 ? 50d : 100d * i / (count - 1);
        }

        return new UsageTapeSeries(name, values);
    }

    // A fresh tape per call: a control cannot be hosted by two windows at once, and a
    // control that has already rendered is not the control a view would hand the window.
    // Both variants every time, because Dark is a designed palette rather than an inversion.

    /// <summary>The tape paints something of its own, in both variants.</summary>
    /// <param name="tape">Builds the tape under test.</param>
    private static void DrawsSomething(Func<UsageTape> tape)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            DesignSystem.AssertRenders(tape(), variant, width: 360d, height: 140d);
        }
    }

    /// <summary>
    /// The tape paints the empty state's picture, pixel for pixel, in both variants.
    /// </summary>
    /// <param name="tape">Builds the tape under test.</param>
    /// <remarks>
    /// "The same as no series" is a claim about the picture, so it is read off the picture: a
    /// tape that drew a flat line at zero for a series of nulls differs from a tape holding
    /// no series at all, and fails here. The comparison is anchored on both sides - neither
    /// tape may paint an empty frame - so two controls that drew nothing cannot satisfy it.
    /// </remarks>
    private static void DrawsTheEmptyState(Func<UsageTape> tape)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            DesignSystem.AssertRendersAlike(
                tape(),
                new UsageTape(),
                variant,
                width: 360d,
                height: 140d);
        }
    }

    /// <summary>
    /// The tape paints a plot rather than the empty state, in both variants.
    /// </summary>
    /// <param name="tape">Builds the tape under test.</param>
    /// <remarks>
    /// A tape that quietly fell back to the empty sentence would paint plenty of ink, so
    /// "it painted something" does not hold this claim. Being a different picture from the
    /// tape with nothing in it does.
    /// </remarks>
    private static void DrawsAPlot(Func<UsageTape> tape)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            DesignSystem.AssertRendersUnlike(
                tape(),
                new UsageTape(),
                variant,
                width: 360d,
                height: 140d);
        }
    }
}
