using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The seam between the face and the words beside it: one state, owned by the dial, resolved
/// in one place, read by both.
/// </summary>
/// <remarks>
/// These are the assertions pixels cannot make. A legend can be drawn correctly while it is
/// bound to a list of its own, or while it is marking rows from its own idea of what the
/// pointer is doing, and both would look right until the two lists differed.
/// </remarks>
public sealed class DialLegendTests
{
    /// <summary>
    /// The legend's rows are the face's arcs, so it cannot end up naming a different set.
    /// Handing the dial another list moves the legend with it.
    /// </summary>
    [AvaloniaFact]
    public void TheLegendTakesItsRowsFromTheFace()
    {
        DialReading[] two = [new(0, "Session (Claude Code)", 62d, 80d), new(1, "Session (Codex)", 41d, 80d)];
        var dial = new Dial { Readings = two };
        DialLegend legend = LegendFor(dial);

        Surface.Show(Stack(dial, legend), window =>
        {
            _ = window;
            Assert.Equal(2, legend.RowCount);
            Assert.Equal(
                ["Session (Claude Code)", "Session (Codex)"],
                RowsOf(legend).Select(row => row.Reading?.Label));

            DialReading[] three =
            [
                new(0, "Weekly (Gemini CLI)", 38d, 80d),
                new(1, "Session (Claude Code)", 62d, 80d),
                new(2, "Session (Codex)", 41d, 80d),
            ];

            dial.Readings = three;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, legend.RowCount);
            Assert.Equal(
                ["Weekly (Gemini CLI)", "Session (Claude Code)", "Session (Codex)"],
                RowsOf(legend).Select(row => row.Reading?.Label));
        });
    }

    /// <summary>
    /// A face carrying one reading has no legend. The words under the figure have already
    /// named it, and with nothing else on the face there is no arc to tell it apart from.
    /// </summary>
    [AvaloniaFact]
    public void AFaceCarryingOneReadingHasNoLegend()
    {
        DialReading[] one = [new(0, "Session (Claude Code)", 62d, 80d)];
        var dial = new Dial { Readings = one };
        DialLegend legend = LegendFor(dial);

        Surface.Show(Stack(dial, legend), window =>
        {
            _ = window;
            Assert.False(legend.IsVisible, "A single ring was given a legend of its own.");

            dial.Readings = [one[0], new DialReading(1, "Session (Codex)", 41d, 80d)];
            Dispatcher.UIThread.RunJobs();

            Assert.True(legend.IsVisible, "A second ring did not bring the legend back.");
        });
    }

    /// <summary>
    /// The panel's own legend is wired to the panel's own dial, which is the wiring every
    /// pixel assertion about the pairing rests on.
    /// </summary>
    [AvaloniaFact]
    public void ThePanelsLegendNamesThePanelsFace()
    {
        using PopupViewModel panel = ThreeProviders();

        Surface.Show(new PopupView { DataContext = panel }, window =>
        {
            DialLegend legend = Assert.Single(window.GetVisualDescendants().OfType<DialLegend>());
            Dial dial = Assert.Single(window.GetVisualDescendants().OfType<Dial>());

            Assert.Same(dial, legend.Face);
            Assert.Equal(3, legend.RowCount);
            Assert.Equal(
                panel.DialReadings,
                RowsOf(legend).Select(row => row.Reading));
        }, width: 320d, height: 900d);
    }

    /// <summary>
    /// The mark is resolved by where a reading stands on the face, not by the number it
    /// carries. The two agree when a builder fills the list and numbers it as it goes, and a
    /// mark that trusted the number would send the hairline to the wrong band the moment they
    /// did not.
    /// </summary>
    [AvaloniaFact]
    public void TheMarkFollowsThePositionOnTheFaceRatherThanTheReadingsOwnNumber()
    {
        DialReading[] arcs =
        [
            new(2, "outer", 60d, 80d),
            new(0, "middle", 40d, 80d),
            new(1, "inner", 20d, 80d),
        ];

        var dial = new Dial { Readings = arcs };

        dial.Mark(arcs[1]);

        Assert.Same(arcs[1], dial.Marked);
        Assert.Equal(1, dial.MarkedRing);
    }

    /// <summary>
    /// A reading this face does not carry is ignored rather than blanking the face. A legend
    /// holding a different list cannot speak for this dial, and clearing the mark on its word
    /// would hide the mistake.
    /// </summary>
    [AvaloniaFact]
    public void AReadingTheFaceDoesNotCarryIsIgnored()
    {
        DialReading[] arcs = [new(0, "outer", 60d, 80d), new(1, "inner", 20d, 80d)];
        var dial = new Dial { Readings = arcs };

        dial.Mark(arcs[0]);
        dial.Mark(new DialReading(0, "somebody else's", 50d, 80d));

        Assert.Same(arcs[0], dial.Marked);
        Assert.Equal(0, dial.MarkedRing);

        // Null still means "stop", which is how a legend lets go.
        dial.Mark(null);
        Assert.Null(dial.Marked);
    }

    /// <summary>
    /// New readings never leave the mark pointing into the list before last: it is resolved
    /// again against the readings that are actually on the face, and let go when the face no
    /// longer reaches that far.
    /// </summary>
    [AvaloniaFact]
    public void ReplacingTheReadingsResolvesTheMarkAgain()
    {
        DialReading[] arcs = [new(0, "a", 60d, 80d), new(1, "b", 40d, 80d), new(2, "c", 20d, 80d)];
        var dial = new Dial { Readings = arcs };

        dial.Mark(arcs[1]);

        DialReading[] fresh = [new(0, "a", 61d, 80d), new(1, "b", 41d, 80d), new(2, "c", 21d, 80d)];
        dial.Readings = fresh;

        Assert.Same(fresh[1], dial.Marked);
        Assert.Equal(1, dial.MarkedRing);

        // And a shorter face lets go rather than marking a band that is not there.
        dial.Readings = new[] { new DialReading(0, "a", 62d, 80d) };

        Assert.Null(dial.Marked);
        Assert.Null(dial.MarkedRing);
    }

    /// <summary>
    /// The three ways of asking are ordered, and letting go of one falls back to the next
    /// rather than to nothing. A pointer on a band is the most direct thing anybody can do to
    /// this face, so it wins; a legend row comes next; the keyboard's own ring is what is left.
    /// </summary>
    [AvaloniaFact]
    public void APointerOnABandOutranksALegendRowWhichOutranksTheKeyboard()
    {
        DialReading[] arcs = [new(0, "a", 60d, 80d), new(1, "b", 40d, 80d), new(2, "c", 20d, 80d)];
        var dial = new Dial { Readings = arcs };

        using PixelHost host = PixelHost.Show(dial, width: 176d, height: 176d);

        Assert.True(dial.Focus(NavigationMethod.Tab), "The dial refused focus.");
        Assert.Equal(0, dial.MarkedRing);

        dial.Mark(arcs[2]);
        Assert.Equal(2, dial.MarkedRing);

        PointAtBand(host, dial, 1);
        Assert.Equal(1, dial.MarkedRing);

        // Leaving the face falls back to the legend row, not to nothing.
        dial.RaiseEvent(new PointerEventArgs(
            InputElement.PointerExitedEvent,
            dial,
            new Pointer(0, PointerType.Mouse, true),
            host.Window,
            default,
            0,
            new PointerPointProperties(),
            KeyModifiers.None));

        Assert.Equal(2, dial.MarkedRing);

        // And letting go of the row falls back to the ring the keyboard is still on.
        dial.Mark(null);
        Assert.Equal(0, dial.MarkedRing);
    }

    /// <summary>
    /// A row is marked because the face says so, never because it is the one under the
    /// pointer. That is what stops the words and the face naming two different readings.
    /// </summary>
    [AvaloniaFact]
    public void ARowIsMarkedByTheFaceRatherThanByItsOwnPointer()
    {
        DialReading[] arcs = [new(0, "a", 60d, 80d), new(1, "b", 40d, 80d)];
        var dial = new Dial { Readings = arcs };
        DialLegend legend = LegendFor(dial);

        Surface.Show(Stack(dial, legend), window =>
        {
            _ = window;
            IReadOnlyList<DialLegendRow> rows = RowsOf(legend);

            // The face is told directly, with nothing happening to the rows at all.
            dial.Mark(arcs[1]);
            Dispatcher.UIThread.RunJobs();

            Assert.False(rows[0].IsMarked);
            Assert.True(rows[1].IsMarked, "The row did not follow the face.");

            dial.Mark(null);
            Dispatcher.UIThread.RunJobs();

            Assert.All(rows, row => Assert.False(row.IsMarked));
        });
    }

    private static void PointAtBand(PixelHost host, Dial dial, int ring)
    {
        int count = dial.Arcs.Count;
        double thickness = Dial.RingThicknessFor(count);
        double radius = (Dial.Size / 2d)
            - Dial.ScaleDepth
            - Dial.ScaleGap
            - (ring * (thickness + Dial.RingGap))
            - (thickness / 2d);

        Rect face = host.BoundsOf(dial);
        double degrees = Dial.AngleFor(20d) * Math.PI / 180d;
        var at = new Point(
            face.X + (face.Width / 2d) + (radius * Math.Sin(degrees)),
            face.Y + (face.Height / 2d) - (radius * Math.Cos(degrees)));

        dial.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            dial,
            new Pointer(0, PointerType.Mouse, true),
            host.Window,
            at,
            0,
            new PointerPointProperties(),
            KeyModifiers.None));
    }

    private static IReadOnlyList<DialLegendRow> RowsOf(DialLegend legend) =>
        legend.GetVisualDescendants().OfType<DialLegendRow>().ToList();

    private static DialLegend LegendFor(Dial dial) => new()
    {
        Face = dial,
        ItemTemplate = new FuncDataTemplate<DialReading>(
            (reading, _) => new DialLegendRow
            {
                Reading = reading,
                Child = new TextBlock { Text = reading?.Label },
            },
            supportsRecycling: false),
    };

    private static Control Stack(Dial dial, DialLegend legend) =>
        new StackPanel { Children = { dial, legend } };

    private static PopupViewModel ThreeProviders()
    {
        FakeUsageProvider[] providers =
        [
            new("claude", "Claude Code", Readings.Healthy("claude")),
            new("codex", "Codex", Readings.Healthy("codex", 41d)),
            new("gemini", "Gemini CLI", Readings.Healthy("gemini", 12d)),
        ];

        var panel = new PopupViewModel(providers, new TestClock(Readings.Now), AltimSettings.Default);
        for (int i = 0; i < providers.Length; i++)
        {
            panel.Providers[i].Apply(providers[i].Reading);
        }

        return panel;
    }
}
