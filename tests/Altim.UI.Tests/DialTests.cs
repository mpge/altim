using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.Themes;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The dial's contract: a level is clamped, an unreported level is not a zero, the angle is a
/// stated function of the level, and the scale is the meter's rather than a second one.
/// </summary>
public sealed class DialTests
{
    /// <summary>The tape, written out, so a change to it has to be made on purpose.</summary>
    private static readonly double[] TheTape =
    [
        0d, 10d, 20d, 30d, 40d, 50d,
        55d, 60d, 65d, 70d, 75d, 80d,
        82.5d, 85d, 87.5d, 90d, 92.5d, 95d, 97.5d, 100d,
    ];

    /// <summary>A level above 100 is clamped rather than sweeping past the ceiling.</summary>
    [AvaloniaFact]
    public void ClampsAboveOneHundred()
    {
        var dial = new Dial { Value = 150d };
        Assert.Equal(100d, dial.Value);
    }

    /// <summary>A negative level is clamped to zero.</summary>
    [AvaloniaFact]
    public void ClampsBelowZero()
    {
        var dial = new Dial { Value = -20d };
        Assert.Equal(0d, dial.Value);
    }

    /// <summary>A new dial has nothing to report until something reports.</summary>
    [AvaloniaFact]
    public void StartsUnavailable()
    {
        var dial = new Dial();

        Assert.Null(dial.Value);
        Assert.True(dial.IsUnavailable);
        Assert.Contains(Dial.UnavailablePseudoClass, dial.Classes);
    }

    /// <summary>Null is unavailable, and NaN is not a level.</summary>
    [AvaloniaFact]
    public void NullAndNotANumberAreUnavailableRatherThanZero()
    {
        var dial = new Dial { Value = 80d };
        Assert.False(dial.IsUnavailable);

        dial.Value = null;
        Assert.True(dial.IsUnavailable);
        Assert.Contains(Dial.UnavailablePseudoClass, dial.Classes);

        dial.Value = double.NaN;
        Assert.Null(dial.Value);
        Assert.True(dial.IsUnavailable);
    }

    /// <summary>The level at or above the threshold sets the class the words beside it read.</summary>
    [AvaloniaFact]
    public void ReportsReachingTheThreshold()
    {
        var dial = new Dial { Value = 79d, Threshold = 80d };
        Assert.False(dial.IsAboveThreshold);
        Assert.DoesNotContain(Dial.AboveThresholdPseudoClass, dial.Classes);

        dial.Value = 80d;
        Assert.True(dial.IsAboveThreshold);
        Assert.Contains(Dial.AboveThresholdPseudoClass, dial.Classes);

        // Unreported is not above anything. A dial with no reading that claimed to be over a
        // threshold would be the zero rule failing in the other direction.
        dial.Value = null;
        Assert.False(dial.IsAboveThreshold);
    }

    /// <summary>
    /// The angle is linear in the level: nothing used at one end of the sweep, the ceiling at
    /// the other, half way in the middle, and an unreported level parked at the start rather
    /// than anywhere that could be mistaken for a reading.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <param name="expected">The angle in degrees clockwise from the top.</param>
    [Theory]
    [InlineData(0d, -120d)]
    [InlineData(25d, -60d)]
    [InlineData(50d, 0d)]
    [InlineData(75d, 60d)]
    [InlineData(100d, 120d)]
    [InlineData(62d, 28.8d)]
    public void TheAngleIsLinearInTheLevel(double level, double expected) =>
        Assert.Equal(expected, Dial.AngleFor(level), 9);

    /// <summary>An unreported level has no angle of its own; the sweep is simply not drawn.</summary>
    [Fact]
    public void AnUnreportedLevelSitsAtTheStartOfTheSweep()
    {
        Assert.Equal(-120d, Dial.AngleFor(null), 9);
        Assert.Equal(Dial.AngleFor(0d), Dial.AngleFor(null), 9);
    }

    /// <summary>The sweep is symmetrical about the top, so neither end is the top.</summary>
    [Fact]
    public void TheSweepIsCentredOnTheTop()
    {
        Assert.Equal(240d, Dial.Sweep);
        Assert.Equal(-Dial.EndAngle, Dial.StartAngle);
        Assert.Equal(Dial.Sweep, Dial.EndAngle - Dial.StartAngle);

        // Not a full circle: the two ends have to be tellable apart.
        Assert.True(Dial.Sweep < 360d, "A full sweep puts nothing used and the ceiling together.");
    }

    /// <summary>
    /// The dial is graduated at exactly the levels the meter is. One scale, two controls: a
    /// card's meters and this panel's dial are read against one another.
    /// </summary>
    [Fact]
    public void TheDialIsGraduatedAtTheMetersLevels()
    {
        Assert.Equal(TheTape, InstrumentScale.Graduations);
        Assert.Equal(TheTape, Meter.Graduations);
        Assert.Equal(TheTape, Dial.GraduationsFor(Dial.Size / 2d));
    }

    /// <summary>
    /// The band is the meter's cross section: same rail, same gap, same graduations, same
    /// index weight, same minimum pitch. Two instruments built to two cross sections would be
    /// two instruments.
    /// </summary>
    [Fact]
    public void TheBandIsTheMetersCrossSection()
    {
        Assert.Equal(8d, Dial.RailThickness);
        Assert.Equal(2d, Dial.ScaleGap);
        Assert.Equal(4d, Dial.ScaleDepth);
        Assert.Equal(14d, Dial.FaceDepth);
        Assert.Equal(2d, Dial.IndexWeight);

        Assert.Equal(Meter.RailHeight, Dial.RailThickness);
        Assert.Equal(Meter.ScaleGap, Dial.ScaleGap);
        Assert.Equal(Meter.ScaleHeight, Dial.ScaleDepth);
        Assert.Equal(Meter.TotalHeight, Dial.FaceDepth);
        Assert.Equal(Meter.IndexWeight, Dial.IndexWeight);
        Assert.Equal(Meter.MinimumGraduationPitch, InstrumentScale.MinimumGraduationPitch);
    }

    /// <summary>
    /// The pitch is measured along the arc where the graduations stand closest together,
    /// which is the radius their inner ends reach - not the face's own edge. Measuring at the
    /// edge would keep marks four pixels apart out there and less than that where they meet.
    /// </summary>
    [Fact]
    public void TheSpanIsTheArcAtTheInnerEndOfTheMarks()
    {
        // 68 is the 72 radius of a 144 face less the 4 the marks run in. 240 degrees of it is
        // 284.838, written out rather than recomputed from the constants under test.
        Assert.Equal(284.838d, Dial.GraduationSpanFor(72d), 3);

        // The face's own edge would give 301.593. A scale fitted to that carries marks it
        // cannot separate where they actually meet.
        Assert.NotEqual(301.593d, Dial.GraduationSpanFor(72d), 3);

        Assert.Equal(0d, Dial.GraduationSpanFor(2d));
        Assert.Empty(Dial.GraduationsFor(0d));
    }

    /// <summary>
    /// The whole tape is carried at the size the panel lays the dial out at, and a face too
    /// small for it thins from the fine end down to the three landmarks rather than crowding.
    /// </summary>
    [Fact]
    public void TheTapeThinsOnASmallFaceAndTheLandmarksGoLast()
    {
        double[] landmarks = [0d, 50d, 100d];

        Assert.Equal(TheTape.Length, Dial.GraduationsFor(Dial.Size / 2d).Count);
        Assert.Equal(landmarks, Dial.GraduationsFor(6d));

        // Every mark a smaller face keeps is one the full tape has, and no two of them stand
        // closer than the four pixels the scale will not go below. Four is written out.
        Assert.Equal(4d, InstrumentScale.MinimumGraduationPitch);

        foreach (double radius in (double[])[20d, 30d, 48d, 72d])
        {
            IReadOnlyList<double> drawn = Dial.GraduationsFor(radius);
            double span = Dial.GraduationSpanFor(radius);

            Assert.All(drawn, level => Assert.Contains(level, TheTape));
            Assert.Contains(0d, drawn);
            Assert.Contains(50d, drawn);
            Assert.Contains(100d, drawn);

            for (int i = 1; i < drawn.Count; i++)
            {
                double pitch = InstrumentScale.PositionFor(drawn[i], span)
                    - InstrumentScale.PositionFor(drawn[i - 1], span);

                Assert.True(
                    pitch >= 4d - 1e-9d,
                    $"On a face of {radius}, {drawn[i - 1]} and {drawn[i]} are {pitch} apart.");
            }
        }
    }

    /// <summary>
    /// The design system's copy of the dial's geometry is the control's. Two numbers that
    /// have to agree and are written down twice drift.
    /// </summary>
    [AvaloniaFact]
    public void TheGeometryTokensAreTheControlsGeometry()
    {
        AltimTheme theme = DesignSystem.LoadStandalone();

        // Written out as well as cross checked, so a pair changed together still has to be
        // changed on purpose rather than drifting to whatever both sides happen to say.
        Assert.Equal(144d, Dial.Size);
        Assert.Equal(144d, Token(theme, "AltimDialSize"));
        Assert.Equal(240d, Token(theme, "AltimDialSweep"));
        Assert.Equal(Dial.Size, Token(theme, "AltimDialSize"));
        Assert.Equal(Dial.Sweep, Token(theme, "AltimDialSweep"));
    }

    /// <summary>The face takes the size it asks for, and stays square when squeezed.</summary>
    [AvaloniaFact]
    public void TheFaceIsSquareAndKeepsItsSize()
    {
        var dial = new Dial { Value = 62d };
        using PixelHost host = PixelHost.Show(
            new StackPanel { Children = { dial } },
            width: 200d,
            height: 200d);

        Assert.Equal(Dial.Size, dial.Bounds.Width, 6);
        Assert.Equal(Dial.Size, dial.Bounds.Height, 6);
        Assert.Equal(Dial.Size / 2d, Dial.FaceRadiusFor(dial.Bounds.Size), 6);

        // Offered less than it wants, it fits rather than overflowing.
        Assert.Equal(40d, Dial.FaceRadiusFor(new Avalonia.Size(80d, 200d)), 6);
    }

    /// <summary>
    /// The reading in words is the meter's, word for word. Two controls showing the same
    /// number must not describe it in two ways, and an unreported reading says so rather than
    /// reading as a zero.
    /// </summary>
    [Fact]
    public void TheReadingIsTheMetersWording()
    {
        Assert.Equal("62% used, threshold 80%", Dial.ReadingFor(62d, 80d));
        Assert.Equal("62% used", Dial.ReadingFor(62d, null));
        Assert.Equal(UsageFormat.MetricUnavailable, Dial.ReadingFor(null, 80d));
        Assert.Equal(Meter.ReadingFor(62d, 80d), Dial.ReadingFor(62d, 80d));
        Assert.NotEqual(Dial.ReadingFor(0d, 80d), Dial.ReadingFor(null, 80d));
    }

    /// <summary>
    /// The dial is a tab stop exactly when it has something the words beside it do not print:
    /// a threshold measured against a level. What it shows is its <see cref="Dial.Detail"/>,
    /// which is the reading plus the sentence explaining the bands: the accessible name stops
    /// at the reading, because a client that receives no colour needs no colour key and would
    /// hear it on every announcement.
    /// </summary>
    [AvaloniaFact]
    public void HoverAndFocusAreOfferedTheSameDetail()
    {
        var reading = new Dial { Value = 62d, Threshold = 80d };
        Assert.True(reading.Focusable);
        Assert.Equal(reading.Detail, ToolTip.GetTip(reading));
        Assert.Equal(
            "62% used, threshold 80%. Caution band. It begins at 50% and the threshold is at 80%.",
            reading.Detail);
        Assert.Equal("62% used, threshold 80%", reading.Reading);

        var unmeasured = new Dial { Value = 62d };
        Assert.False(unmeasured.Focusable);
        Assert.Null(ToolTip.GetTip(unmeasured));

        var unreported = new Dial { Threshold = 80d };
        Assert.False(unreported.Focusable);
        Assert.Null(ToolTip.GetTip(unreported));

        reading.Threshold = null;
        Assert.False(reading.Focusable);
        Assert.Null(ToolTip.GetTip(reading));
    }

    /// <summary>Focus opens the tip a pointer opens, and losing it closes the tip.</summary>
    [AvaloniaFact]
    public void FocusOpensTheTipAPointerWouldOpen()
    {
        var dial = new Dial { Value = 62d, Threshold = 80d };
        var away = new Button { Content = "Away" };

        using PixelHost host = PixelHost.Show(
            new StackPanel { Children = { dial, away } },
            width: 200d,
            height: 240d);

        Assert.False(ToolTip.GetIsOpen(dial));
        Assert.True(dial.Focus(NavigationMethod.Tab), "The dial refused focus.");
        Assert.True(ToolTip.GetIsOpen(dial), "Focus set the dial's words but never showed them.");

        Assert.True(away.Focus(NavigationMethod.Tab), "The button refused focus.");
        Assert.False(ToolTip.GetIsOpen(dial));
    }

    /// <summary>The peer reads the level and the threshold, after any name the view set.</summary>
    [AvaloniaFact]
    public void ThePeerReadsTheLevelAndTheThreshold()
    {
        var dial = new Dial { Value = 62d, Threshold = 80d };
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(dial);

        Assert.Equal("62% used, threshold 80%", peer.GetName());
        Assert.Equal(nameof(Dial), peer.GetClassName());
        Assert.Equal(AutomationControlType.ProgressBar, peer.GetAutomationControlType());

        AutomationProperties.SetName(dial, "Session (Claude Code)");
        Assert.Equal("Session (Claude Code), 62% used, threshold 80%", peer.GetName());
    }

    /// <summary>
    /// The peer never hands a client a number for a reading nobody reported. There is no
    /// range value pattern for exactly that reason, and the two states read differently.
    /// </summary>
    [AvaloniaFact]
    public void ThePeerNeverReadsAnUnreportedReadingAsZero()
    {
        var unreported = new Dial { Threshold = 80d };
        var zero = new Dial { Value = 0d, Threshold = 80d };

        AutomationPeer unreportedPeer = ControlAutomationPeer.CreatePeerForElement(unreported);
        AutomationPeer zeroPeer = ControlAutomationPeer.CreatePeerForElement(zero);

        Assert.Equal(UsageFormat.MetricUnavailable, unreportedPeer.GetName());
        Assert.Equal("0% used, threshold 80%", zeroPeer.GetName());
        Assert.NotEqual(unreportedPeer.GetName(), zeroPeer.GetName());
        Assert.Null(unreportedPeer.GetProvider<Avalonia.Automation.Provider.IRangeValueProvider>());
    }

    /// <summary>A new reading is announced, because there is no value pattern to carry it.</summary>
    [AvaloniaFact]
    public void ThePeerAnnouncesANewReading()
    {
        var dial = new Dial { Value = 62d, Threshold = 80d };
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(dial);

        int announced = 0;
        peer.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationElementIdentifiers.NameProperty)
            {
                announced++;
            }
        };

        dial.Value = 71d;
        Assert.Equal(1, announced);
        Assert.Equal("71% used, threshold 80%", peer.GetName());

        // The same reading again says nothing. A screen reader repeating an unchanged figure
        // is noise somebody has to listen through.
        dial.Value = 71d;
        Assert.Equal(1, announced);
    }

    /// <summary>
    /// The paths are built once and kept. The panel this sits on is shown rather than built,
    /// and a dial that rebuilt its geometry every time the panel repainted would spend the
    /// opening budget redrawing a picture that had not changed.
    /// </summary>
    [AvaloniaFact]
    public void ThePathsAreNotRebuiltWhileNothingMoves()
    {
        var dial = new Dial { Value = 62d, Threshold = 80d };
        using PixelHost host = PixelHost.Show(
            new StackPanel { Children = { dial } },
            width: 200d,
            height: 200d);

        _ = host.Capture();
        Avalonia.Media.Geometry? band = dial.RenderedBand;
        Avalonia.Media.Geometry? sweep = dial.RenderedSweep;
        Assert.NotNull(band);
        Assert.NotNull(sweep);

        // A repaint that changes nothing about the reading reuses both paths.
        dial.InvalidateVisual();
        _ = host.Capture();
        Assert.Same(band, dial.RenderedBand);
        Assert.Same(sweep, dial.RenderedSweep);

        // A new level rebuilds them, because the sweep is a different shape.
        dial.Value = 71d;
        _ = host.Capture();
        Assert.NotSame(band, dial.RenderedBand);
        Assert.NotSame(sweep, dial.RenderedSweep);

        // So does the face moving inside the control. A dial given more height than width
        // keeps the same face and centres it lower, and a path cached on the radius alone
        // would go on being drawn where the face used to be.
        var stretched = new Dial { Value = 62d, Threshold = 80d, Width = 144d, Height = 200d };
        using PixelHost tall = PixelHost.Show(
            new StackPanel { Children = { stretched } },
            width: 240d,
            height: 320d);

        _ = tall.Capture();
        Avalonia.Media.Geometry? centred = stretched.RenderedBand;
        Assert.NotNull(centred);
        Assert.Equal(72d, Dial.FaceRadiusFor(stretched.Bounds.Size), 6);

        stretched.Height = 280d;
        tall.Window.UpdateLayout();
        _ = tall.Capture();

        Assert.Equal(72d, Dial.FaceRadiusFor(stretched.Bounds.Size), 6);
        Assert.NotSame(centred, stretched.RenderedBand);
    }

    /// <summary>
    /// Nothing used and nothing reported both draw no sweep, and for the same reason: a sweep
    /// of no length is not a reading. What tells them apart is the rail behind it, which the
    /// pixel tests read.
    /// </summary>
    [AvaloniaFact]
    public void NeitherNothingUsedNorNothingReportedDrawsASweep()
    {
        var zero = new Dial { Value = 0d, Threshold = 80d };
        var unreported = new Dial { Threshold = 80d };

        using PixelHost host = PixelHost.Show(
            new StackPanel { Children = { zero, unreported } },
            width: 200d,
            height: 400d);

        _ = host.Capture();

        Assert.Null(zero.RenderedSweep);
        Assert.Null(unreported.RenderedSweep);

        // The band is still built for both: it is what the unavailable state outlines.
        Assert.NotNull(zero.RenderedBand);
        Assert.NotNull(unreported.RenderedBand);

        // And the smallest reported level there is does draw one.
        zero.Value = 1d;
        _ = host.Capture();
        Assert.NotNull(zero.RenderedSweep);
    }

    private static double Token(AltimTheme theme, string key)
    {
        Assert.True(
            theme.TryGetResource(key, ThemeVariant.Light, out object? value),
            $"{key} does not resolve.");
        return Assert.IsType<double>(value);
    }
}
