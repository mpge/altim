using Altim.UI.Controls;
using Avalonia.Headless.XUnit;
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
    public void LevelsAreTwentyFiveFiftySeventyFiveAndOneHundred() =>
        Assert.Equal<double>([25d, 50d, 75d, 100d], UsageTape.Levels);

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

    /// <summary>A tape with no series renders the empty state instead of an empty plot.</summary>
    [AvaloniaFact]
    public void RendersWithNoSeries() => RenderBothVariants(() => new UsageTape());

    /// <summary>An empty series list is the same as no series.</summary>
    [AvaloniaFact]
    public void RendersWithAnEmptySeriesList() =>
        RenderBothVariants(() => new UsageTape { Series = [] });

    /// <summary>A series with no samples in it is the same as no series.</summary>
    [AvaloniaFact]
    public void RendersWithASeriesThatHasNoSamples() =>
        RenderBothVariants(() => new UsageTape { Series = [new UsageTapeSeries("Claude", [])] });

    /// <summary>
    /// A series where every sample is unreported draws nothing rather than a flat line at
    /// zero. A null is a null.
    /// </summary>
    [AvaloniaFact]
    public void RendersWithASeriesOfOnlyUnreportedSamples() =>
        RenderBothVariants(
            () => new UsageTape { Series = [new UsageTapeSeries("Claude", [null, null, null])] });

    /// <summary>One sample is a dot and a name, not a crash and not a line.</summary>
    [AvaloniaFact]
    public void RendersASinglePointSeries() =>
        RenderBothVariants(() => new UsageTape { Series = [new UsageTapeSeries("Claude", [42d])] });

    /// <summary>Two samples are the shortest thing that is actually a line.</summary>
    [AvaloniaFact]
    public void RendersATwoPointSeries() =>
        RenderBothVariants(() => new UsageTape { Series = [new UsageTapeSeries("Claude", [10d, 90d])] });

    /// <summary>A gap breaks the line rather than dropping it to zero.</summary>
    [AvaloniaFact]
    public void RendersASeriesWithGaps() =>
        RenderBothVariants(
            () => new UsageTape
            {
                Series = [new UsageTapeSeries("Claude", [10d, null, 40d, null, null, 80d])],
            });

    /// <summary>Sample counts on both sides of the dot limit render.</summary>
    /// <param name="count">The number of samples.</param>
    [AvaloniaTheory]
    [InlineData(3)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(256)]
    public void RendersAcrossTheDotLimit(int count) =>
        RenderBothVariants(() => new UsageTape { Series = [Ramp("Claude", count)] });

    /// <summary>Two providers, one primary and one secondary, named at the end of each line.</summary>
    [AvaloniaFact]
    public void RendersTwoSeries() =>
        RenderBothVariants(
            () => new UsageTape
            {
                Series =
                [
                    Ramp("Claude", 12),
                    new UsageTapeSeries("Codex", [80d, 70d, 55d, 55d, 30d], UsageTapeEmphasis.Secondary),
                ],
            });

    /// <summary>A tape squeezed to nothing does not throw.</summary>
    [AvaloniaFact]
    public void RendersWithNoRoomToDrawIn() =>
        DesignSystem.AssertRenders(
            new UsageTape { Series = [Ramp("Claude", 8)] },
            width: 2d,
            height: 2d);

    /// <summary>Level labels can be turned off without the plot losing its gutters.</summary>
    [AvaloniaFact]
    public void RendersWithoutLevelLabels() =>
        RenderBothVariants(
            () => new UsageTape { ShowLevelLabels = false, Series = [Ramp("Claude", 8)] });

    /// <summary>Out of range samples are clamped into the plot rather than drawn outside it.</summary>
    [AvaloniaFact]
    public void RendersOutOfRangeSamples() =>
        RenderBothVariants(
            () => new UsageTape { Series = [new UsageTapeSeries("Claude", [-40d, 240d, double.NaN, 50d])] });

    private static UsageTapeSeries Ramp(string name, int count)
    {
        var values = new double?[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = count == 1 ? 50d : 100d * i / (count - 1);
        }

        return new UsageTapeSeries(name, values);
    }

    // A fresh tape per variant: a control cannot be hosted by two windows at once, and a
    // control that has already rendered is not the control a view would hand the window.
    private static void RenderBothVariants(Func<UsageTape> tape)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            DesignSystem.AssertRenders(tape(), variant, width: 360d, height: 140d);
        }
    }
}
