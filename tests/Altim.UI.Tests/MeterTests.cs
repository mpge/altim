using Altim.UI.Controls;
using Altim.UI.Themes;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Animation.Easings;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The meter's contract: a level is clamped, an unreported level is not a zero, and the
/// fill width is a stated function of the level and the rail width rather than something
/// only the renderer knows.
/// </summary>
public sealed class MeterTests
{
    /// <summary>A level above 100 is clamped rather than overflowing the rail.</summary>
    [AvaloniaFact]
    public void ClampsAboveOneHundred()
    {
        var meter = new Meter { Value = 150d };
        Assert.Equal(100d, meter.Value);
    }

    /// <summary>A negative level is clamped to zero.</summary>
    [AvaloniaFact]
    public void ClampsBelowZero()
    {
        var meter = new Meter { Value = -20d };
        Assert.Equal(0d, meter.Value);
    }

    /// <summary>A level inside the range is left exactly as it was given.</summary>
    [AvaloniaFact]
    public void KeepsALevelInsideTheRange()
    {
        var meter = new Meter { Value = 62.5d };
        Assert.Equal(62.5d, meter.Value);
    }

    /// <summary>A new meter has nothing to report until something reports.</summary>
    [AvaloniaFact]
    public void StartsUnavailable()
    {
        var meter = new Meter();

        Assert.Null(meter.Value);
        Assert.True(meter.IsUnavailable);
        Assert.Contains(Meter.UnavailablePseudoClass, meter.Classes);
    }

    /// <summary>Null is unavailable, and unavailable draws no fill.</summary>
    [AvaloniaFact]
    public void NullIsUnavailableAndNotZero()
    {
        var meter = new Meter { Value = 80d };
        Assert.False(meter.IsUnavailable);

        meter.Value = null;

        Assert.True(meter.IsUnavailable);
        Assert.Equal(0d, meter.RenderedFillWidth);
        Assert.Contains(Meter.UnavailablePseudoClass, meter.Classes);
    }

    /// <summary>NaN is not a level, so it is treated as unreported.</summary>
    [AvaloniaFact]
    public void NotANumberIsUnavailable()
    {
        var meter = new Meter { Value = double.NaN };

        Assert.Null(meter.Value);
        Assert.True(meter.IsUnavailable);
    }

    /// <summary>The fill width mapping is linear, with no minimum and no rounding.</summary>
    /// <param name="value">The level.</param>
    /// <param name="width">The rail width.</param>
    /// <param name="expected">The expected fill width.</param>
    [Theory]
    [InlineData(0d, 200d, 0d)]
    [InlineData(1d, 200d, 2d)]
    [InlineData(25d, 200d, 50d)]
    [InlineData(50d, 200d, 100d)]
    [InlineData(100d, 200d, 200d)]
    [InlineData(80d, 320d, 256d)]
    [InlineData(150d, 200d, 200d)]
    [InlineData(-5d, 200d, 0d)]
    [InlineData(50d, 0d, 0d)]
    public void FillWidthIsALinearFunctionOfTheLevel(double value, double width, double expected) =>
        Assert.Equal(expected, Meter.FillWidthFor(value, width), 6);

    /// <summary>An unreported level has no fill width at any size.</summary>
    [AvaloniaFact]
    public void FillWidthOfAnUnreportedLevelIsZero() =>
        Assert.Equal(0d, Meter.FillWidthFor(null, 200d));

    /// <summary>
    /// A laid out meter reports the fill width the renderer draws. The window is 200 wide
    /// and the meter stretches into it, so 40% is 80px.
    /// </summary>
    [AvaloniaFact]
    public void ReportsTheRenderedFillWidthOnceLaidOut()
    {
        var meter = new Meter { Value = 40d };

        // Hosted in a stack the way a metric row hosts it, so the rail takes the height it
        // asked for rather than the height the window had spare.
        var row = new StackPanel();
        row.Children.Add(meter);

        using (WriteableBitmap frame = DesignSystem.Render(row, width: 200d, height: 32d))
        {
            Assert.True(frame.PixelSize.Width > 0);
        }

        Assert.Equal(200d, meter.Bounds.Width, 6);
        Assert.Equal(Meter.RailHeight, meter.Bounds.Height, 6);
        Assert.Equal(80d, meter.RenderedFillWidth, 6);
    }

    /// <summary>The rail is the 6px the brief specifies, whatever it is given.</summary>
    [AvaloniaFact]
    public void RailIsSixPixelsTall()
    {
        var meter = new Meter { Value = 10d };
        var row = new StackPanel();
        row.Children.Add(meter);

        using (WriteableBitmap frame = DesignSystem.Render(row, width: 240d, height: 96d))
        {
            Assert.True(frame.PixelSize.Height > 0);
        }

        Assert.Equal(8d, Meter.RailHeight);
        Assert.Equal(8d, meter.Bounds.Height, 6);
    }

    /// <summary>The first value lands flat: an entrance is not a level changing.</summary>
    [AvaloniaFact]
    public void TheFirstValueDoesNotAnimate()
    {
        var meter = new Meter { Value = 40d };

        Assert.Null(meter.Transitions);
        Assert.Equal(40d, meter.DisplayValue);
    }

    /// <summary>Coming back from unavailable lands flat as well.</summary>
    [AvaloniaFact]
    public void ReturningFromUnavailableDoesNotAnimate()
    {
        var meter = new Meter { Value = 40d };
        meter.Value = null;
        Assert.Equal(0d, meter.DisplayValue);

        meter.Value = 70d;

        Assert.Equal(70d, meter.DisplayValue);
    }

    /// <summary>
    /// Moving between two reported levels animates, for 180ms, on an ease-out curve.
    /// </summary>
    [AvaloniaFact]
    public void AChangedValueAnimatesForOneHundredAndEightyMillisecondsEaseOut()
    {
        var meter = new Meter { Value = 40d };
        meter.Value = 90d;

        Transitions transitions = Assert.IsType<Transitions>(meter.Transitions);
        var transition = Assert.IsType<DoubleTransition>(Assert.Single(transitions));

        Assert.Same(Meter.DisplayValueProperty, transition.Property);
        Assert.Equal(TimeSpan.FromMilliseconds(180d), transition.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(180d), Meter.FillDuration);
        Assert.IsType<CubicEaseOut>(transition.Easing);
    }

    /// <summary>A level at or above the threshold is reported, so the label can react.</summary>
    [AvaloniaFact]
    public void ReportsCrossingTheThreshold()
    {
        var meter = new Meter { Threshold = 80d, Value = 79d };
        Assert.False(meter.IsAboveThreshold);

        meter.Value = 80d;
        Assert.True(meter.IsAboveThreshold);
        Assert.Contains(Meter.AboveThresholdPseudoClass, meter.Classes);

        meter.Value = null;
        Assert.False(meter.IsAboveThreshold);
    }

    /// <summary>A meter with no threshold never claims to be above one.</summary>
    [AvaloniaFact]
    public void WithoutAThresholdItIsNeverAboveOne()
    {
        var meter = new Meter { Value = 100d };
        Assert.False(meter.IsAboveThreshold);
    }

    /// <summary>Every state the meter has renders a frame, in both variants.</summary>
    /// <param name="value">The level, or null for unavailable.</param>
    /// <param name="threshold">The threshold, or null for none.</param>
    [AvaloniaTheory]
    [InlineData(null, null)]
    [InlineData(null, 80d)]
    [InlineData(0d, 80d)]
    [InlineData(0.4d, 80d)]
    [InlineData(55d, null)]
    [InlineData(80d, 80d)]
    [InlineData(100d, 80d)]
    public void RendersEveryStateInBothVariants(double? value, double? threshold)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            DesignSystem.AssertRenders(
                new Meter { Value = value, Threshold = threshold },
                variant,
                width: 240d,
                height: 32d);
        }
    }

    /// <summary>A meter with no room to draw in does not throw.</summary>
    [AvaloniaFact]
    public void RendersAtZeroWidth() =>
        DesignSystem.AssertRenders(new Meter { Value = 50d }, width: 1d, height: 1d);

    /// <summary>
    /// A meter asks for a width. It reports a level by how far its fill runs, so one laid
    /// out at zero width reports nothing at all - and an auto sized column will do exactly
    /// that, with no layout error and nothing on screen to notice.
    /// </summary>
    [AvaloniaFact]
    public void AskedForNoRoomItStillAsksForItsMinimumWidth()
    {
        var meter = new Meter { Value = 40d };

        // An auto sized column measures its children with infinite width.
        var column = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto") };
        column.Children.Add(meter);

        using (WriteableBitmap frame = DesignSystem.Render(column, width: 200d, height: 32d))
        {
            Assert.True(frame.PixelSize.Width > 0);
        }

        Assert.Equal(48d, Meter.MinimumWidth);
        Assert.True(
            meter.Bounds.Width >= Meter.MinimumWidth,
            $"An auto sized column laid the meter out at {meter.Bounds.Width}.");
        Assert.True(meter.RenderedFillWidth > 0d, "A meter with a level drew no fill.");
    }

    /// <summary>
    /// Offered unlimited room the meter asks for its floor, and offered less than that it
    /// asks for what it was offered, so the floor can never push a layout into overflow.
    /// </summary>
    [AvaloniaFact]
    public void ItAsksForItsFloorButNeverForMoreRoomThanItWasOffered()
    {
        var unbounded = new Meter { Value = 40d };
        unbounded.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(Meter.MinimumWidth, unbounded.DesiredSize.Width, 6);
        Assert.Equal(Meter.RailHeight, unbounded.DesiredSize.Height, 6);

        var squeezed = new Meter { Value = 40d };
        squeezed.Measure(new Size(12d, 32d));
        Assert.Equal(12d, squeezed.DesiredSize.Width, 6);
    }

    /// <summary>
    /// The design system's copy of the minimum width is the control's. Two numbers that
    /// have to agree and are written down twice always drift, so this is the guard.
    /// </summary>
    [AvaloniaFact]
    public void TheMinimumWidthTokenIsTheControlsMinimumWidth()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        Assert.True(
            theme.TryGetResource("AltimMeterMinWidth", ThemeVariant.Light, out object? token),
            "AltimMeterMinWidth does not resolve.");
        Assert.Equal(Meter.MinimumWidth, Assert.IsType<double>(token));
    }
}
