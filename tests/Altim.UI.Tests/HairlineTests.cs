using Altim.UI.Controls;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The arithmetic behind the one line weight in the system.
/// </summary>
/// <remarks>
/// A 1px rule is one device independent pixel, which is 1.25 device pixels on a 125%
/// display and 1.5 on a 150% one. Neither can be filled, so the rasteriser spreads the
/// line over two rows at partial coverage: the hairline comes out grey rather than black,
/// and a different grey on each rule depending on where it landed. Rounding the weight to a
/// whole number of device pixels and putting its edge on a device pixel boundary is the fix,
/// and both need the render scaling, which is why this is arithmetic rather than a constant.
/// </remarks>
public sealed class HairlineTests
{
    /// <summary>The nominal weight is the 1px DESIGN.md specifies.</summary>
    [Fact]
    public void TheNominalWeightIsOnePixel() => Assert.Equal(1d, Hairline.Nominal);

    /// <summary>
    /// The weight is whatever covers a whole number of device pixels at the given scale.
    /// </summary>
    /// <param name="scale">The render scaling.</param>
    /// <param name="expected">The expected weight in device independent pixels.</param>
    [Theory]
    [InlineData(1d, 1d)]
    [InlineData(1.25d, 0.8d)]
    [InlineData(1.5d, 1.3333333333333333d)]
    [InlineData(1.75d, 1.1428571428571428d)]
    [InlineData(2d, 1d)]
    [InlineData(3d, 1d)]
    public void TheWeightCoversAWholeNumberOfDevicePixels(double scale, double expected)
    {
        double weight = Hairline.ThicknessFor(scale);

        Assert.Equal(expected, weight, 9);
        Assert.Equal(Math.Round(weight * scale), weight * scale, 9);
    }

    /// <summary>A rule never rounds away to nothing, however dense the display.</summary>
    /// <param name="scale">The render scaling.</param>
    [Theory]
    [InlineData(0.5d)]
    [InlineData(0.75d)]
    [InlineData(1d)]
    [InlineData(4d)]
    public void TheWeightIsNeverLessThanOneDevicePixel(double scale) =>
        Assert.True(Hairline.ThicknessFor(scale) * scale >= 1d - 1e-9d);

    /// <summary>A scale that is not a number falls back to the nominal weight.</summary>
    /// <param name="scale">The broken scaling.</param>
    [Theory]
    [InlineData(0d)]
    [InlineData(-2d)]
    [InlineData(double.NaN)]
    public void ABrokenScaleFallsBackToTheNominalWeight(double scale) =>
        Assert.Equal(Hairline.Nominal, Hairline.ThicknessFor(scale));

    /// <summary>An edge lands on a device pixel boundary.</summary>
    /// <param name="position">The wanted edge.</param>
    /// <param name="scale">The render scaling.</param>
    /// <param name="expected">The snapped edge.</param>
    [Theory]
    [InlineData(10d, 1d, 10d)]
    [InlineData(10.4d, 1d, 10d)]
    [InlineData(10.5d, 1d, 11d)]
    [InlineData(10.1d, 1.25d, 10.4d)]
    [InlineData(10d, 1.5d, 10d)]
    [InlineData(10.2d, 1.5d, 10d)]
    [InlineData(10.4d, 1.5d, 10d + (2d / 3d))]
    public void AnEdgeLandsOnADevicePixelBoundary(double position, double scale, double expected)
    {
        double snapped = Hairline.SnapEdge(position, scale);

        Assert.Equal(expected, snapped, 9);
        Assert.Equal(Math.Round(snapped * scale), snapped * scale, 9);
    }

    /// <summary>A centred rule is placed by its leading edge, still on the grid.</summary>
    [Fact]
    public void ACentredRuleIsPlacedByItsLeadingEdge()
    {
        double weight = Hairline.ThicknessFor(1.25d);
        double edge = Hairline.SnapCentre(20d, weight, 1.25d);

        Assert.Equal(Math.Round(edge * 1.25d), edge * 1.25d, 9);
        Assert.True(Math.Abs(edge + (weight / 2d) - 20d) <= weight, "The rule moved more than its own weight.");
    }

    /// <summary>A visual with no root is drawn at 100% until it has one.</summary>
    [Fact]
    public void AVisualWithNoRootScalesAtOne() => Assert.Equal(1d, Hairline.ScaleOf(null));
}
