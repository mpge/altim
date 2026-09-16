using Altim.Core.Models;
using Altim.Platform.MacOS;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The parts of the macOS layer that do not need a Mac.
/// </summary>
/// <remarks>
/// Everything in <c>Altim.Platform.MacOS</c> that touches <c>objc_msgSend</c> is unverified
/// and cannot be tested here. What can be, and is, is the arithmetic and string handling that
/// interop feeds: the bottom-left to top-left flip, the bundle path shape, and the template
/// asset choice. Those are the three places a mistake would look like a rendering bug rather
/// than a crash.
/// </remarks>
public sealed class MacOSCoordinatesTests
{
    [Fact]
    public void StatusItemAtTheTopOfThePrimaryScreenFlipsToTheOrigin()
    {
        // A 1080-point display with a 22-point menu bar: the item's window sits with its
        // bottom edge at 1058 and its top edge at the very top of the screen.
        PixelRect? anchor = MacOSCoordinates.ToPixelRect(
            x: 1200, y: 1058, width: 24, height: 22, primaryScreenHeight: 1080, scale: 1);

        Assert.Equal(new PixelRect(1200, 0, 24, 22), anchor);
    }

    [Fact]
    public void TheFlipSubtractsTheTopEdgeAndNotTheOrigin()
    {
        // Deliberately not flush with the top: 1080 - (1000 + 22) = 58. Subtracting the
        // origin alone would give 80, which is the height of the item out of place and the
        // exact bug this test exists to catch.
        PixelRect? anchor = MacOSCoordinates.ToPixelRect(
            x: 0, y: 1000, width: 24, height: 22, primaryScreenHeight: 1080, scale: 1);

        Assert.Equal(58, anchor!.Value.Y);
    }

    [Fact]
    public void RetinaScalingMultipliesEveryComponent()
    {
        PixelRect? anchor = MacOSCoordinates.ToPixelRect(
            x: 600, y: 878, width: 24, height: 22, primaryScreenHeight: 900, scale: 2);

        Assert.Equal(new PixelRect(1200, 0, 48, 44), anchor);
    }

    [Fact]
    public void ADisplayAboveThePrimaryOneProducesANegativeY()
    {
        // Cocoa's origin is the bottom left of the primary screen, so a screen arranged above
        // it has coordinates past the primary screen's height. In a top-left space that is a
        // negative Y, which is correct and must not be clamped to zero.
        PixelRect? anchor = MacOSCoordinates.ToPixelRect(
            x: 0, y: 2000, width: 24, height: 22, primaryScreenHeight: 1080, scale: 1);

        Assert.True(anchor!.Value.Y < 0);
    }

    [Theory]
    [InlineData(0d, 22d, 1080d, 1d)]      // no width
    [InlineData(24d, 0d, 1080d, 1d)]      // no height
    [InlineData(24d, 22d, 0d, 1d)]        // no screen
    [InlineData(double.NaN, 22d, 1080d, 1d)]
    [InlineData(24d, double.PositiveInfinity, 1080d, 1d)]
    public void AnUnusableRectangleIsNullRatherThanEmpty(
        double width, double height, double primaryScreenHeight, double scale) =>
        Assert.Null(MacOSCoordinates.ToPixelRect(0, 0, width, height, primaryScreenHeight, scale));

    [Fact]
    public void ANonsensicalScaleFallsBackToOneRatherThanCollapsing()
    {
        PixelRect? anchor = MacOSCoordinates.ToPixelRect(
            x: 100, y: 1058, width: 24, height: 22, primaryScreenHeight: 1080, scale: 0);

        Assert.Equal(new PixelRect(100, 0, 24, 22), anchor);
    }
}

/// <summary>Bundle detection, which decides whether two macOS APIs may be called at all.</summary>
public sealed class MacOSAppBundleTests
{
    [Fact]
    public void ABundledExecutableIsRecognised() =>
        Assert.True(MacOSAppBundle.LooksBundled("/Applications/Altim.app/Contents/MacOS/Altim"));

    [Fact]
    public void TheBundleRootIsTheDotAppDirectory() =>
        Assert.Equal(
            "/Applications/Altim.app",
            MacOSAppBundle.FindBundleRoot("/Applications/Altim.app/Contents/MacOS/Altim"));

    [Fact]
    public void ResourcesSitInsideTheBundle() =>
        Assert.Equal(
            "/Applications/Altim.app/Contents/Resources",
            MacOSAppBundle.FindResourcesDirectory("/Applications/Altim.app/Contents/MacOS/Altim"));

    [Fact]
    public void ContentsMacOSWithoutADotAppParentIsNotABundle() =>
        Assert.False(MacOSAppBundle.LooksBundled("/tmp/Contents/MacOS/Altim"));

    [Fact]
    public void ADotnetRunOutputIsNotABundle() =>
        Assert.False(MacOSAppBundle.LooksBundled("/src/altim/bin/Debug/net10.0/Altim"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsNotABundle(string? path) => Assert.False(MacOSAppBundle.LooksBundled(path));
}

/// <summary>Menu bar asset selection.</summary>
public sealed class MacOSTrayAssetsTests
{
    [Theory]
    [InlineData(1d, 18)]
    [InlineData(2d, 36)]
    [InlineData(1.5d, 27)]
    [InlineData(0d, 18)]
    [InlineData(double.NaN, 18)]
    public void ThePixelSizeFollowsTheBackingScaleFactor(double scale, int expected) =>
        Assert.Equal(expected, MacOSTrayAssets.PixelSizeForScale(scale));

    [Theory]
    [InlineData(16, 16)]
    [InlineData(18, 20)]   // rounds up: downscaling keeps the glyph, upscaling loses hinting
    [InlineData(36, 44)]
    [InlineData(1024, 512)] // nothing is big enough; take the largest there is
    public void TheAssetChosenIsNeverSmallerThanAsked(int wanted, int expected) =>
        Assert.Equal(expected, MacOSTrayAssets.ChooseAssetSize(wanted));

    [Fact]
    public void EveryChosenSizeIsOneThatActuallyExists()
    {
        for (int wanted = 1; wanted <= 600; wanted++)
        {
            Assert.Contains(MacOSTrayAssets.ChooseAssetSize(wanted), MacOSTrayAssets.Sizes);
        }
    }

    [Fact]
    public void TheFileNameIsTheTemplateRendering() =>
        Assert.Equal("altim-template-20.png", MacOSTrayAssets.FileName(20));
}
