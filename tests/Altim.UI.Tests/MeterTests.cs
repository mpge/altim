using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.UI.Accessibility;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.Themes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Animation.Easings;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
        Assert.Equal(Meter.TotalHeight, meter.Bounds.Height, 6);
        Assert.Equal(80d, meter.RenderedFillWidth, 6);
    }

    /// <summary>
    /// The rail is the 8px the brief specifies and the scale is hung under it, so the
    /// control stands at 14 whatever it is given.
    /// </summary>
    [AvaloniaFact]
    public void TheRailIsEightTallAndTheScaleHangsUnderIt()
    {
        var meter = new Meter { Value = 10d };
        var row = new StackPanel();
        row.Children.Add(meter);

        using (WriteableBitmap frame = DesignSystem.Render(row, width: 240d, height: 96d))
        {
            Assert.True(frame.PixelSize.Height > 0);
        }

        Assert.Equal(8d, Meter.RailHeight);
        Assert.Equal(2d, Meter.ScaleGap);
        Assert.Equal(4d, Meter.ScaleHeight);
        Assert.Equal(14d, Meter.TotalHeight);
        Assert.Equal(14d, meter.Bounds.Height, 6);
    }

    /// <summary>
    /// An unreported metric measures the same as a reported one. A control that shrank when
    /// it had nothing to say would reflow the page every time a provider came back.
    /// </summary>
    [AvaloniaFact]
    public void AnUnreportedMetricTakesTheSameRoomAsAReportedOne()
    {
        var reported = new Meter { Value = 62d, Threshold = 80d };
        var unreported = new Meter { Value = null, Threshold = 80d };

        reported.Measure(new Size(240d, 240d));
        unreported.Measure(new Size(240d, 240d));

        Assert.Equal(reported.DesiredSize, unreported.DesiredSize);
        Assert.Equal(Meter.TotalHeight, unreported.DesiredSize.Height, 6);
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
        using MotionScope scope = MotionScope.Of(MotionPreference.Full);

        var meter = new Meter { Value = 40d };
        meter.Value = 90d;

        Transitions transitions = Assert.IsType<Transitions>(meter.Transitions);
        var transition = Assert.IsType<DoubleTransition>(Assert.Single(transitions));

        Assert.Same(Meter.DisplayValueProperty, transition.Property);
        Assert.Equal(TimeSpan.FromMilliseconds(180d), transition.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(180d), Meter.FillDuration);
        Assert.IsType<CubicEaseOut>(transition.Easing);
    }

    /// <summary>
    /// A machine that has asked for reduced motion gets the new level and no travel to it.
    /// The level is not withheld and it is not delayed: it is the drawn value immediately.
    /// </summary>
    /// <remarks>
    /// On screen, because an unhosted meter has no transitions running whatever it is told:
    /// Avalonia enables them on attachment. Asserted off screen this would pass against an
    /// implementation that ignored the preference entirely.
    /// </remarks>
    /// <param name="preference">The reading the platform reported.</param>
    [AvaloniaTheory]
    [InlineData(MotionPreference.Reduced)]
    [InlineData(MotionPreference.Unknown)]
    public void AChangedValueLandsFlatWhenMotionIsNotAllowed(MotionPreference preference)
    {
        using MotionScope scope = MotionScope.Of(preference);

        var meter = new Meter { Value = 40d };
        using PixelHost host = PixelHost.Show(new StackPanel { Children = { meter } });

        meter.Value = 90d;

        Assert.Null(meter.Transitions);
        Assert.Equal(90d, meter.DisplayValue);
    }

    /// <summary>
    /// With motion allowed the same change does not land flat, which is what makes the
    /// assertion above about suppression rather than about the meter never animating.
    /// </summary>
    /// <remarks>
    /// On screen, because Avalonia enables a control's transitions when it is attached to a
    /// visual tree and not before: an unhosted meter takes every value straight away whatever
    /// its transitions say, which would make this pass for the wrong reason.
    /// </remarks>
    [AvaloniaFact]
    public void TheSameChangeDoesNotLandFlatWhenMotionIsAllowed()
    {
        using MotionScope scope = MotionScope.Of(MotionPreference.Full);

        var meter = new Meter { Value = 40d };
        using PixelHost host = PixelHost.Show(new StackPanel { Children = { meter } });

        meter.Value = 90d;

        Assert.NotNull(meter.Transitions);
        Assert.NotEqual(90d, meter.DisplayValue);
    }

    /// <summary>
    /// A fill already in the air is cut short when the machine asks for reduced motion, at
    /// the value it was travelling to. Somebody switching the setting on gets relief from
    /// the animation that is on screen, not only from the next one.
    /// </summary>
    [AvaloniaFact]
    public void ReducingMotionMidFlightCutsTheTravelShortAtTheNewLevel()
    {
        using MotionScope scope = MotionScope.Of(MotionPreference.Full);

        var meter = new Meter { Value = 40d, Threshold = 80d };

        // On screen, because a meter listens for the preference only while it is attached.
        using PixelHost host = PixelHost.Show(new StackPanel { Children = { meter } });

        meter.Value = 90d;
        Assert.NotEqual(90d, meter.DisplayValue);

        Motion.Set(MotionPreference.Reduced);

        Assert.Equal(90d, meter.DisplayValue);

        // And it stays there: a cut that only held for one frame would be handed straight
        // back to the animation on the next tick.
        Frame frame = host.Capture();
        Assert.True(frame.Width > 0);
        Assert.Equal(90d, meter.DisplayValue);
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
        Assert.Equal(Meter.TotalHeight, unbounded.DesiredSize.Height, 6);

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

    /// <summary>
    /// The design system's copy of the meter's geometry is the control's. The same reason as
    /// the minimum width: two numbers that have to agree and are written down twice drift.
    /// </summary>
    [AvaloniaFact]
    public void TheGeometryTokensAreTheControlsGeometry()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        Assert.Equal(Meter.TotalHeight, Token(theme, "AltimMeterHeight"));
        Assert.Equal(Meter.RailHeight, Token(theme, "AltimMeterRailHeight"));
        Assert.Equal(Meter.ScaleGap, Token(theme, "AltimMeterScaleGap"));
        Assert.Equal(Meter.ScaleHeight, Token(theme, "AltimMeterScaleHeight"));
    }

    /// <summary>
    /// The tape is the stated one: every 10 to half way, every 5 on to the threshold Altim
    /// ships with, every 2.5 over the last fifth.
    /// </summary>
    [Fact]
    public void TheScaleIsGraduatedInThreeBands()
    {
        double[] expected =
        [
            0d, 10d, 20d, 30d, 40d, 50d,
            55d, 60d, 65d, 70d, 75d, 80d,
            82.5d, 85d, 87.5d, 90d, 92.5d, 95d, 97.5d, 100d,
        ];

        Assert.Equal(expected, Meter.Graduations);
    }

    /// <summary>
    /// The interval never widens on the way up, and the last fifth of the rail carries three
    /// times the graduations the first fifth does. A tape that was even, or that tightened
    /// at the wrong end, would satisfy every other assertion about it here.
    /// </summary>
    [Fact]
    public void TheIntervalTightensTowardTheCeiling()
    {
        IReadOnlyList<double> levels = Meter.Graduations;

        for (int i = 2; i < levels.Count; i++)
        {
            double previous = levels[i - 1] - levels[i - 2];
            double current = levels[i] - levels[i - 1];

            Assert.True(
                current <= previous + 1e-9d,
                $"The interval widens from {previous} to {current} at {levels[i]}.");
        }

        Assert.Equal(10d, levels[1] - levels[0]);
        Assert.Equal(2.5d, levels[^1] - levels[^2]);
        Assert.Equal(3, levels.Count(level => level <= 20d));
        Assert.Equal(9, levels.Count(level => level >= 80d));
    }

    /// <summary>Half way and the ceiling are the levels the history tape also rules at.</summary>
    [Fact]
    public void TheMajorGraduationsAreTheLevelsTheTapeRules()
    {
        Assert.True(Meter.IsMajorGraduation(0d));
        Assert.True(Meter.IsMajorGraduation(50d));
        Assert.True(Meter.IsMajorGraduation(100d));
        Assert.False(Meter.IsMajorGraduation(80d));
        Assert.False(Meter.IsMajorGraduation(97.5d));
    }

    /// <summary>
    /// A narrower rail thins the tape rather than crowding it. Two graduations closer than
    /// the minimum pitch read as one thick mark, which would report a solid block exactly
    /// where the scale is finest.
    /// </summary>
    /// <param name="width">The rail width to fit the scale to.</param>
    [Theory]
    [InlineData(48d)]
    [InlineData(80d)]
    [InlineData(160d)]
    [InlineData(240d)]
    [InlineData(516d)]
    public void GraduationsThinRatherThanCrowd(double width)
    {
        IReadOnlyList<double> drawn = Meter.GraduationsFor(width);

        Assert.All(drawn, level => Assert.Contains(level, Meter.Graduations));
        Assert.Contains(0d, drawn);
        Assert.Contains(50d, drawn);
        Assert.Contains(100d, drawn);

        // Four, written out, rather than the constant the implementation uses: a test that
        // measures the rule against itself passes however the rule is changed.
        Assert.Equal(4d, Meter.MinimumGraduationPitch);

        for (int i = 1; i < drawn.Count; i++)
        {
            double pitch = Meter.FillWidthFor(drawn[i], width) - Meter.FillWidthFor(drawn[i - 1], width);

            Assert.True(
                pitch >= 4d - 1e-9d,
                $"At {width} wide, {drawn[i - 1]} and {drawn[i]} are {pitch} apart.");
        }
    }

    /// <summary>
    /// The fine band is what a narrow rail gives up first, and the three landmarks are what
    /// is left at the floor. The whole tape is carried from 160 up, which is well under the
    /// narrowest meter the application lays out.
    /// </summary>
    [Fact]
    public void TheFineBandGoesFirstAndTheLandmarksGoLast()
    {
        double[] landmarks = [0d, 50d, 100d];

        Assert.Equal(Meter.Graduations.Count, Meter.GraduationsFor(160d).Count);
        Assert.Equal(16, Meter.GraduationsFor(80d).Count);
        Assert.Equal(11, Meter.GraduationsFor(Meter.MinimumWidth).Count);
        Assert.Equal(landmarks, Meter.GraduationsFor(8d));
        Assert.Empty(Meter.GraduationsFor(0d));
        Assert.Empty(Meter.GraduationsFor(double.NaN));
    }

    /// <summary>
    /// The narrowest meter the application can actually produce is the one in a provider card
    /// on the Overview, with the window shrunk to the width it refuses to go below. It still
    /// has room for the whole tape, fine band included.
    /// </summary>
    [AvaloniaFact]
    public async Task TheNarrowestMeterTheWindowCanProduceCarriesTheWholeTape()
    {
        IUsageProvider[] providers =
        [
            new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude")),
            new FakeUsageProvider("codex", "Codex", Readings.Healthy("codex", 41d)),
        ];

        using var dashboard = new DashboardViewModel(
            providers,
            new FakeHistoryService(),
            new FakeSettingsStore(),
            new FakeStatusLineService(),
            new TestClock(Readings.Now));

        await dashboard.LoadAsync(TestContext.Current.CancellationToken);

        var window = new DashboardWindow(dashboard) { Height = 720d };
        window.Width = window.MinWidth;

        double narrowest = double.PositiveInfinity;

        Surface.Render(window, _ =>
        {
            IReadOnlyList<Meter> meters = Surface.Visible<Meter>(window);
            Assert.NotEmpty(meters);
            narrowest = meters.Min(meter => meter.Bounds.Width);
        });

        Assert.Equal(820d, window.MinWidth);
        Assert.True(
            narrowest >= 160d,
            $"The narrowest meter on screen is {narrowest} wide, which cannot carry the fine band.");
        Assert.Equal(Meter.Graduations.Count, Meter.GraduationsFor(narrowest).Count);
    }

    /// <summary>
    /// What the meter says in words. The threshold is named, because an index on a scale is
    /// no use to somebody who cannot see which level it stands at, and an unreported metric
    /// says so rather than reading as a nought.
    /// </summary>
    [Fact]
    public void TheReadingNamesTheLevelAndTheThreshold()
    {
        Assert.Equal("62% used, threshold 80%", Meter.ReadingFor(62d, 80d));
        Assert.Equal("62% used", Meter.ReadingFor(62d, null));
        Assert.Equal("0% used, threshold 80%", Meter.ReadingFor(0d, 80d));
        Assert.Equal("100% used, threshold 80%", Meter.ReadingFor(100d, 80d));
        Assert.Equal(UsageFormat.MetricUnavailable, Meter.ReadingFor(null, 80d));
        Assert.NotEqual(Meter.ReadingFor(0d, 80d), Meter.ReadingFor(null, 80d));
    }

    /// <summary>
    /// A meter is a tab stop exactly when it has something the row around it does not print,
    /// and the tip it carries is the same one either way in.
    /// </summary>
    [AvaloniaFact]
    public void HoverAndFocusAreOfferedTheSameDetail()
    {
        var reading = new Meter { Value = 62d, Threshold = 80d };
        Assert.True(reading.Focusable);
        Assert.Equal("62% used, threshold 80%", ToolTip.GetTip(reading));

        // Nothing but the figure already printed beside it: not worth a stop in the tab
        // order, and so not worth a tip either, or the two would disagree.
        var unmeasured = new Meter { Value = 62d };
        Assert.False(unmeasured.Focusable);
        Assert.Null(ToolTip.GetTip(unmeasured));

        var unreported = new Meter { Value = null, Threshold = 80d };
        Assert.False(unreported.Focusable);
        Assert.Null(ToolTip.GetTip(unreported));

        reading.Value = null;
        Assert.False(reading.Focusable);
        Assert.Null(ToolTip.GetTip(reading));
    }

    /// <summary>
    /// Focus opens the tip a pointer opens. Avalonia's tip service is armed by the pointer
    /// alone, so a meter that merely set a tip would answer a mouse and ignore a keyboard.
    /// </summary>
    [AvaloniaFact]
    public void FocusOpensTheTipAPointerWouldOpen()
    {
        var meter = new Meter { Value = 62d, Threshold = 80d };
        var row = new StackPanel();
        row.Children.Add(meter);

        using PixelHost host = PixelHost.Show(row, width: 240d, height: 60d);

        Assert.False(ToolTip.GetIsOpen(meter));
        Assert.True(meter.Focus(NavigationMethod.Tab), "The meter refused focus.");
        Assert.True(ToolTip.GetIsOpen(meter), "Focus set the meter's words but never showed them.");
        Assert.Equal(meter.Reading, ToolTip.GetTip(meter));

        var away = new Button { Content = "Retry" };
        row.Children.Add(away);
        Assert.True(away.Focus(NavigationMethod.Tab), "The button refused focus.");
        Assert.False(ToolTip.GetIsOpen(meter), "The tip stayed open after focus left the meter.");
    }

    /// <summary>
    /// The meter is named and valued to an assistive technology, which it was not before: a
    /// drawn rail has no name at all. A view that names it keeps its name, with the reading
    /// after it.
    /// </summary>
    [AvaloniaFact]
    public void ThePeerReadsTheLevelAndTheThreshold()
    {
        var meter = new Meter { Value = 62d, Threshold = 80d };
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(meter);

        Assert.Equal("62% used, threshold 80%", peer.GetName());
        Assert.Equal(AutomationControlType.ProgressBar, peer.GetAutomationControlType());
        Assert.Equal(nameof(Meter), peer.GetClassName());

        AutomationProperties.SetName(meter, "Session");
        Assert.Equal("Session, 62% used, threshold 80%", peer.GetName());
    }

    /// <summary>
    /// An unreported metric is not a zero to a screen reader either, which is why the peer
    /// offers no range value pattern: that pattern carries a double, and a double cannot say
    /// "nobody reported this".
    /// </summary>
    [AvaloniaFact]
    public void ThePeerNeverReadsAnUnreportedMetricAsZero()
    {
        var meter = new Meter { Value = null, Threshold = 80d };
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(meter);

        Assert.Equal(UsageFormat.MetricUnavailable, peer.GetName());
        Assert.Null(peer.GetProvider<IRangeValueProvider>());

        var zero = new Meter { Value = 0d, Threshold = 80d };
        AutomationPeer zeroPeer = ControlAutomationPeer.CreatePeerForElement(zero);

        Assert.NotEqual(peer.GetName(), zeroPeer.GetName());
    }

    /// <summary>A new reading is announced, because nothing else would tell a client.</summary>
    [AvaloniaFact]
    public void ThePeerAnnouncesANewReading()
    {
        var meter = new Meter { Value = 62d, Threshold = 80d };
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(meter);

        int announcements = 0;
        peer.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationElementIdentifiers.NameProperty)
            {
                announcements++;
            }
        };

        meter.Value = 91d;
        Assert.Equal(1, announcements);
        Assert.Equal("91% used, threshold 80%", peer.GetName());

        meter.Value = 91d;
        Assert.Equal(1, announcements);
    }

    private static double Token(AltimTheme theme, string key)
    {
        Assert.True(
            theme.TryGetResource(key, ThemeVariant.Light, out object? value),
            $"{key} does not resolve.");
        return Assert.IsType<double>(value);
    }
}
