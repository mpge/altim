using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.Themes;
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
/// The dial carrying more than one reading: where the rings go, what order they are in, what
/// the bands mean, and the one arithmetic this control is never allowed to do.
/// </summary>
/// <remarks>
/// The pixels are in <see cref="DialRingPixelTests"/>. What is asserted here is the contract
/// the renderer positions: a wrong ring order, a combined figure or a band boundary that had
/// drifted off the scale's own landmark would all be invisible to a test that only read the
/// screen and found two arcs on it.
/// </remarks>
public sealed class DialRingTests
{
    /// <summary>
    /// The face carries the readings in the order it was given them, outermost first, and
    /// never reorders them by level.
    /// </summary>
    /// <remarks>
    /// Registration order is what keeps the picture still. Ordering by "nearest its ceiling"
    /// instead would swap two rings every time the numbers crossed, so a ring somebody had
    /// learned to read as one provider's would quietly become the other's, and the swap would
    /// happen exactly when the numbers were worth watching.
    /// </remarks>
    [AvaloniaFact]
    public void TheRingsStayInTheOrderTheyWereGivenEvenWhenTheLevelsCross()
    {
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", 33d, 80d),
                new DialReading(1, "Weekly (Codex)", 54d, 80d),
            ],
        };

        Assert.Equal(2, dial.Arcs.Count);
        Assert.Equal("Session (Claude Code)", dial.Arcs[0].Label);
        Assert.Equal("Weekly (Codex)", dial.Arcs[1].Label);

        // The lower reading is outermost and stays outermost when it overtakes the other.
        dial.Readings =
        [
            new DialReading(0, "Session (Claude Code)", 91d, 80d),
            new DialReading(1, "Weekly (Codex)", 54d, 80d),
        ];

        Assert.Equal("Session (Claude Code)", dial.Arcs[0].Label);
        Assert.Equal(0, dial.Arcs[0].Ring);
        Assert.Equal(1, dial.Arcs[1].Ring);
    }

    /// <summary>
    /// <b>No figure anywhere is two providers' figures put together.</b> The rings share a
    /// face because they share a unit, not because their numbers were added: 33 per cent of
    /// one vendor's allowance and 54 per cent of another's are proportions of two different,
    /// undisclosed limits, and their sum, mean and difference all measure nothing.
    /// </summary>
    /// <remarks>
    /// Written as a rule about the words the dial produces rather than about an
    /// implementation, because the failure this guards against is somebody adding a
    /// "combined" figure later and it looking perfectly reasonable in a review.
    /// </remarks>
    [AvaloniaFact]
    public void NothingOnTheFaceIsTwoProvidersFiguresPutTogether()
    {
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", 33d, 80d),
                new DialReading(1, "Weekly (Codex)", 54d, 80d),
            ],
        };

        string words = string.Join(" ", dial.Reading, dial.Detail);

        Assert.Contains("33%", words, StringComparison.Ordinal);
        Assert.Contains("54%", words, StringComparison.Ordinal);

        // The sum, the mean, the difference and the mean of the two rounded either way.
        foreach (string invented in (string[])["87%", "44%", "43%", "21%", "45%"])
        {
            Assert.DoesNotContain(invented, words, StringComparison.Ordinal);
        }

        // And the control offers nothing that is one number over the whole face.
        Assert.Null(dial.Value);
    }

    /// <summary>
    /// The rings thin as the face takes more of them, and the engraving never moves: the tape
    /// is fitted to the face, not to a ring, so two dials carrying different numbers of
    /// providers are still read against the same marks.
    /// </summary>
    [Fact]
    public void TheRingsThinAndTheTapeStaysWhereItIs()
    {
        Assert.Equal(8d, Dial.RingThicknessFor(1));
        Assert.Equal(5d, Dial.RingThicknessFor(2));
        Assert.Equal(3d, Dial.RingThicknessFor(3));

        // A single reading is the meter's own rail, to the pixel. That is the whole reason a
        // card's meters and its dial can be read against one another.
        Assert.Equal(Meter.RailHeight, Dial.RingThicknessFor(1));

        Assert.Equal(14d, Dial.FaceDepthFor(1));
        Assert.Equal(18d, Dial.FaceDepthFor(2));
        Assert.Equal(19d, Dial.FaceDepthFor(3));
        Assert.Equal(Dial.FaceDepth, Dial.FaceDepthFor(1));

        // Every radius is a whole number, so no ring is snapped a pixel thinner than the one
        // outside it when the face is drawn.
        foreach (int count in (int[])[1, 2, 3])
        {
            Assert.Equal(0d, Dial.RingThicknessFor(count) % 1d);
            Assert.Equal(0d, Dial.FaceDepthFor(count) % 1d);
        }

        // The tape is the face's, whatever the face is carrying.
        Assert.Equal(284.838d, Dial.GraduationSpanFor(Dial.Size / 2d), 3);
        Assert.Equal(20, Dial.GraduationsFor(Dial.Size / 2d).Count);
    }

    /// <summary>
    /// Three rings is what a 144 face carries, and the measurement that says so is the figure
    /// standing inside it.
    /// </summary>
    /// <remarks>
    /// "100%" is 95 wide at the type scale's Figure and its ink stands 12 either side of the
    /// middle, so what has to clear the innermost ring is not the clear circle's diameter but
    /// its chord at that height. One ring leaves 9 either side, two leave 5, three leave 4 and
    /// a fourth leaves less than nothing: the figure would be standing on the instrument. This
    /// is the number to look at when a fourth provider arrives.
    /// </remarks>
    [Fact]
    public void TheFaceCarriesThreeRingsBeforeTheFigureRunsOutOfRoom()
    {
        const double figureWidth = 95d;
        const double figureHalfHeight = 12d;

        static double Clearance(int count)
        {
            double inner = (Dial.Size / 2d) - Dial.FaceDepthFor(count);
            double chord = 2d * Math.Sqrt((inner * inner) - (figureHalfHeight * figureHalfHeight));
            return (chord - figureWidth) / 2d;
        }

        Assert.Equal(9.2d, Clearance(1), 1);
        Assert.Equal(5.1d, Clearance(2), 1);
        Assert.Equal(4.1d, Clearance(3), 1);
        Assert.True(Clearance(4) < 0d, "A fourth ring leaves the figure room it does not need.");

        // And a ring is never drawn thinner than a band can be read at, so the fourth does not
        // buy its room by making every ring a rule.
        Assert.Equal(Dial.MinimumRingThickness, Dial.RingThicknessFor(4));
        Assert.Equal(3d, Dial.MinimumRingThickness);
    }

    /// <summary>
    /// The legend's marks are the rings, at the rings' own places in the stack: the widest
    /// circle names the outermost arc, and each one inward is smaller.
    /// </summary>
    [AvaloniaFact]
    public void TheLegendMarksShrinkInwardWithTheRings()
    {
        Assert.Equal(14d, new DialReading(0, "a", 10d).LegendMarkDiameter);
        Assert.Equal(10d, new DialReading(1, "b", 10d).LegendMarkDiameter);
        Assert.Equal(6d, new DialReading(2, "c", 10d).LegendMarkDiameter);

        // Never smaller than a mark that can still be seen, however deep the stack gets.
        Assert.Equal(Dial.LegendMarkStep, new DialReading(9, "d", 10d).LegendMarkDiameter);

        // The design system's copy of the box is the control's.
        AltimTheme theme = DesignSystem.LoadStandalone();
        Assert.True(
            theme.TryGetResource("AltimDialLegendMarkSize", ThemeVariant.Light, out object? size),
            "AltimDialLegendMarkSize does not resolve.");
        Assert.Equal(14d, Assert.IsType<double>(size));
        Assert.Equal(Dial.LegendMarkSize, Assert.IsType<double>(size));
    }

    /// <summary>
    /// The bands begin at the two levels the product already treats as meaningful: the scale's
    /// own half way landmark, and the configured threshold.
    /// </summary>
    /// <remarks>
    /// Both are levels the face already draws a mark at, which is what keeps colour redundant
    /// here: half way is a full depth graduation and the threshold is the index. A boundary at
    /// a level with no mark on it would be a boundary only colour could report.
    /// </remarks>
    [Fact]
    public void TheBandsBeginAtTheScalesLandmarkAndAtTheThreshold()
    {
        Assert.Equal(50d, InstrumentScale.HalfWay);
        Assert.True(InstrumentScale.IsMajorGraduation(InstrumentScale.HalfWay));
        Assert.Contains(InstrumentScale.HalfWay, InstrumentScale.Graduations);

        Assert.Equal(50d, DialBands.CautionFrom(80d));
        Assert.Equal(80d, DialBands.ExceededFrom(80d));
        Assert.Contains(80d, InstrumentScale.Graduations);

        // No threshold, no exceeded band: there is nothing to have exceeded, and inventing a
        // limit is the one thing this product does not do.
        Assert.Equal(50d, DialBands.CautionFrom(null));
        Assert.Null(DialBands.ExceededFrom(null));

        // A threshold below half way collapses the caution band rather than reordering them.
        Assert.Equal(40d, DialBands.CautionFrom(40d));
        Assert.Equal(40d, DialBands.ExceededFrom(40d));
    }

    /// <summary>Which band a level falls in, including the edges and the unreported case.</summary>
    /// <param name="level">The level, or null.</param>
    /// <param name="threshold">The configured threshold, or null.</param>
    /// <param name="expected">The band, written as its name, or null.</param>
    [Theory]
    [InlineData(0d, 80d, "Normal")]
    [InlineData(49.9d, 80d, "Normal")]
    [InlineData(50d, 80d, "Caution")]
    [InlineData(79.9d, 80d, "Caution")]
    [InlineData(80d, 80d, "Exceeded")]
    [InlineData(100d, 80d, "Exceeded")]
    [InlineData(90d, null, "Caution")]
    [InlineData(null, 80d, null)]
    public void TheBandIsDecidedByWhereTheReadingHasGot(double? level, double? threshold, string? expected)
    {
        DialBand? band = DialBands.BandFor(level, threshold);

        Assert.Equal(expected, band?.ToString());
    }

    /// <summary>
    /// The bands are named in words, with both boundaries, so the colour is never a code
    /// somebody has to learn. This is what a pointer and the keyboard reveal.
    /// </summary>
    [Fact]
    public void TheBandWordsNameTheBandAndBothBoundaries()
    {
        Assert.Equal(
            "Normal band. Caution begins at 50% and the threshold at 80%.",
            DialBands.Words(20d, 80d));
        Assert.Equal(
            "Caution band. It begins at 50% and the threshold is at 80%.",
            DialBands.Words(62d, 80d));
        Assert.Equal(
            "Past the 80% threshold. Caution begins at 50%.",
            DialBands.Words(92d, 80d));
        Assert.Equal("Caution band. It begins at 50%.", DialBands.Words(62d, null));

        // Nothing reported is in no band, and is told so rather than placed in one.
        Assert.Null(DialBands.Words(null, 80d));
    }

    /// <summary>
    /// A ring with no figure keeps its place, its name and its words, and is never a zero. It
    /// draws no sweep, which the pixels check; here it is what it says.
    /// </summary>
    [AvaloniaFact]
    public void AnUnreportedRingIsNamedAsUnreportedRatherThanZero()
    {
        var reading = new DialReading(1, "Session (Codex)", null, 80d, "Resets in 2h 14m");

        Assert.False(reading.IsReported);
        Assert.Equal(UsageFormat.Unknown, reading.PercentText);
        Assert.Null(reading.Band);
        Assert.Equal($"Session (Codex), {UsageFormat.MetricUnavailable}", reading.Words);
        Assert.DoesNotContain("0%", reading.Detailed, StringComparison.Ordinal);

        var zero = new DialReading(1, "Session (Codex)", 0d, 80d, "Resets in 2h 14m");
        Assert.NotEqual(zero.Words, reading.Words);
        Assert.NotEqual(zero.PercentText, reading.PercentText);
    }

    /// <summary>
    /// The peer names every reading on the face, and the whole dial is unavailable only when
    /// nothing anywhere reports.
    /// </summary>
    [AvaloniaFact]
    public void ThePeerNamesEveryReadingOnTheFace()
    {
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", 62d, 80d, "Resets in 2h 14m"),
                new DialReading(1, "Session (Codex)", null, 80d, "Resets in 4h"),
            ],
        };

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(dial);

        Assert.Equal(
            $"Session (Claude Code), 62% used, threshold 80%. Session (Codex), "
                + UsageFormat.MetricUnavailable,
            peer.GetName());

        // One ring reporting is enough for the face to be a reading, and it is above its
        // threshold only when a reported one actually is.
        Assert.False(dial.IsUnavailable);
        Assert.False(dial.IsAboveThreshold);

        dial.Readings =
        [
            new DialReading(0, "Session (Claude Code)", null, 80d),
            new DialReading(1, "Session (Codex)", null, 80d),
        ];

        Assert.True(dial.IsUnavailable);
        Assert.Contains(Dial.UnavailablePseudoClass, dial.Classes);

        dial.Readings =
        [
            new DialReading(0, "Session (Claude Code)", 88d, 80d),
            new DialReading(1, "Session (Codex)", 12d, 80d),
        ];

        Assert.True(dial.IsAboveThreshold);
        Assert.Contains(Dial.AboveThresholdPseudoClass, dial.Classes);
    }

    /// <summary>
    /// A face where nothing at all is reported can still be asked which ring is whose, and
    /// answers with every provider named as unreported.
    /// </summary>
    /// <remarks>
    /// The rings are all outlines then, so nothing but their words says which provider each
    /// one belongs to. A ring that could be marked and not named would be the one thing on
    /// this face that pointed at nothing.
    /// </remarks>
    [AvaloniaFact]
    public void AFaceWithNothingReportedCanStillSayWhichRingIsWhose()
    {
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", null, 80d),
                new DialReading(1, "Session (Codex)", null, 80d),
            ],
        };

        using PixelHost host = PixelHost.Show(
            new StackPanel { Children = { dial } },
            width: 200d,
            height: 220d);

        Assert.True(dial.IsUnavailable);
        Assert.True(dial.Focusable, "A face carrying two providers cannot be asked which is which.");

        Assert.True(dial.Focus(NavigationMethod.Tab), "The dial refused focus.");
        Assert.Equal(0, dial.FocusedRing);
        Assert.Equal(
            $"Session (Claude Code), {UsageFormat.MetricUnavailable}.",
            ToolTip.GetTip(dial));

        Press(dial, Key.Down);
        Assert.Equal($"Session (Codex), {UsageFormat.MetricUnavailable}.", ToolTip.GetTip(dial));

        // And still never a zero anywhere in what it says.
        Assert.DoesNotContain("0%", dial.Reading, StringComparison.Ordinal);
    }

    /// <summary>
    /// A new reading is announced once, because there is no value pattern to carry one, and a
    /// list that says the same thing says nothing.
    /// </summary>
    [AvaloniaFact]
    public void ThePeerAnnouncesANewSetOfReadings()
    {
        var dial = new Dial
        {
            Readings = [new DialReading(0, "Session (Claude Code)", 62d, 80d)],
        };

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(dial);
        int announced = 0;
        peer.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Automation.AutomationElementIdentifiers.NameProperty)
            {
                announced++;
            }
        };

        dial.Readings = [new DialReading(0, "Session (Claude Code)", 71d, 80d)];
        Assert.Equal(1, announced);
        Assert.Equal("Session (Claude Code), 71% used, threshold 80%", peer.GetName());

        dial.Readings = [new DialReading(0, "Session (Claude Code)", 71d, 80d)];
        Assert.Equal(1, announced);
    }

    /// <summary>
    /// Focus reveals what a pointer reveals, and the arrows walk the rings. A dial whose rings
    /// could only be told apart by pointing at them would be a dial a keyboard cannot read.
    /// </summary>
    [AvaloniaFact]
    public void TheKeyboardWalksTheRingsAndIsToldWhatEachOneReads()
    {
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", 62d, 80d, "Resets in 2h 14m"),
                new DialReading(1, "Session (Codex)", 41d, 80d, "Resets in 4h"),
            ],
        };

        using PixelHost host = PixelHost.Show(
            new StackPanel { Children = { dial } },
            width: 200d,
            height: 220d);

        Assert.True(dial.Focusable);
        Assert.Null(dial.FocusedRing);

        Assert.True(dial.Focus(NavigationMethod.Tab), "The dial refused focus.");
        Assert.Equal(0, dial.FocusedRing);
        Assert.True(ToolTip.GetIsOpen(dial), "Focus never showed the words it set.");
        Assert.Equal(dial.Arcs[0].Detailed, ToolTip.GetTip(dial));
        Assert.Contains("Session (Claude Code)", dial.Arcs[0].Detailed, StringComparison.Ordinal);
        Assert.Contains("Resets in 2h 14m", dial.Arcs[0].Detailed, StringComparison.Ordinal);
        Assert.Contains("Caution band", dial.Arcs[0].Detailed, StringComparison.Ordinal);

        Press(dial, Key.Down);
        Assert.Equal(1, dial.FocusedRing);
        Assert.Equal(dial.Arcs[1].Detailed, ToolTip.GetTip(dial));
        Assert.Contains("Session (Codex)", dial.Arcs[1].Detailed, StringComparison.Ordinal);

        // The ends hold rather than wrapping: an arrow that came back round would read out a
        // ring the reader thought they had left behind.
        Press(dial, Key.Down);
        Assert.Equal(1, dial.FocusedRing);

        Press(dial, Key.Up);
        Assert.Equal(0, dial.FocusedRing);

        Press(dial, Key.End);
        Assert.Equal(1, dial.FocusedRing);

        Press(dial, Key.Home);
        Assert.Equal(0, dial.FocusedRing);
    }

    /// <summary>
    /// Pointing at a ring says whose it is, what it reads and when its window rolls over, and
    /// pointing at the ring beside it says the other's. Pointing at a ring and being told
    /// nothing is the failure this control has to avoid: the arcs are the only thing on the
    /// face that is not labelled where it stands.
    /// </summary>
    [AvaloniaFact]
    public void PointingAtARingSaysWhoseItIsAndWhatItReads()
    {
        var dial = new Dial
        {
            Readings =
            [
                new DialReading(0, "Session (Claude Code)", 33d, 80d, "Resets in 2h 14m"),
                new DialReading(1, "Weekly (Codex)", 54d, 80d, "Resets in 3d"),
            ],
        };

        using PixelHost host = PixelHost.Show(
            new StackPanel { Children = { dial } },
            width: 200d,
            height: 220d);

        Rect face = host.BoundsOf(dial);
        Point centre = face.Center;

        // The middle of each ring's band, out at the top of the face where both are drawn.
        host.Window.MouseMove(OnRing(centre, 0), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, dial.HoveredRing);
        Assert.Equal(dial.Arcs[0].Detailed, ToolTip.GetTip(dial));
        Assert.True(ToolTip.GetIsOpen(dial), "Pointing at a ring set its words but never showed them.");
        Assert.Contains("Session (Claude Code)", dial.Arcs[0].Detailed, StringComparison.Ordinal);
        Assert.Contains("Resets in 2h 14m", dial.Arcs[0].Detailed, StringComparison.Ordinal);
        Assert.Contains("Normal band", dial.Arcs[0].Detailed, StringComparison.Ordinal);
        Assert.Contains("Caution begins at 50%", dial.Arcs[0].Detailed, StringComparison.Ordinal);

        host.Window.MouseMove(OnRing(centre, 1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, dial.HoveredRing);
        Assert.Equal(dial.Arcs[1].Detailed, ToolTip.GetTip(dial));
        Assert.Contains("Weekly (Codex)", dial.Arcs[1].Detailed, StringComparison.Ordinal);
        Assert.Contains("Caution band", dial.Arcs[1].Detailed, StringComparison.Ordinal);

        // The middle of the face is the figure's room and belongs to no ring, so pointing
        // there explains the whole face rather than one arc of it.
        host.Window.MouseMove(centre, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(dial.HoveredRing);
        Assert.Contains("Session (Claude Code)", ToolTip.GetTip(dial)?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Weekly (Codex)", ToolTip.GetTip(dial)?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The middle of one ring's band, at the top of the face.</summary>
    private static Point OnRing(Point centre, int ring)
    {
        double thickness = Dial.RingThicknessFor(2);
        double radius = (Dial.Size / 2d)
            - Dial.ScaleDepth
            - Dial.ScaleGap
            - (ring * (thickness + Dial.RingGap))
            - (thickness / 2d);

        return new Point(centre.X, centre.Y - radius);
    }

    /// <summary>
    /// A single dial does not swallow the arrow keys. It sits on a card inside the Overview's
    /// scrolling area, and one that handled them would stop the page scrolling to say nothing.
    /// </summary>
    [AvaloniaFact]
    public void ASingleRingLeavesTheArrowKeysAlone()
    {
        var dial = new Dial { Value = 62d, Threshold = 80d };

        using PixelHost host = PixelHost.Show(
            new StackPanel { Children = { dial } },
            width: 200d,
            height: 220d);

        Assert.True(dial.Focus(NavigationMethod.Tab), "The dial refused focus.");

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Down,
            Source = dial,
        };

        dial.RaiseEvent(args);
        Assert.False(args.Handled);
    }

    private static void Press(Dial dial, Key key) =>
        dial.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            Source = dial,
        });
}
