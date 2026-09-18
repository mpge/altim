using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.Tests.Fakes;
using Altim.UI.ViewModels;
using Altim.UI.Views;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The Overview card headed by a dial: which window it carries, that it belongs to the provider
/// whose card it stands on, and that two of them side by side can still be read against one
/// another.
/// </summary>
/// <remarks>
/// <para>
/// A gauge suits one reading against a target and several gauges side by side are the pattern
/// that is advised against, because gauges do not compare. What makes these two compare is that
/// they are one instrument on one scale at one size, drawn at the same height in both cards, and
/// that each names the window it is carrying - so the reader can see when the two dials are
/// measuring the same window and when they are not. Every one of those is asserted here rather
/// than assumed, because each of them is one property away from being false.
/// </para>
/// <para>
/// Every captured frame is copied out and the platform bitmap disposed before the assertion
/// runs - see <see cref="Frame"/> - because an undisposed frame takes the renderer down.
/// </para>
/// </remarks>
public sealed class ProviderCardTests
{
    /// <summary>
    /// The window the dial carries is the highest one <b>this</b> provider reports, and not the
    /// first one it happens to list. A rule run across every provider would head both cards
    /// with Claude Code's 62, and one that took the first window would head Codex's card with
    /// its 20 rather than the 38 that is actually nearest its ceiling.
    /// </summary>
    [Fact]
    public void EachCardsDialCarriesItsOwnProvidersHighestWindow()
    {
        using ProviderViewModel claude = Row("claude", "Claude Code", Readings.Healthy("claude"));

        // Session 20, Weekly 38: the highest window is the second one reported.
        using ProviderViewModel codex = Row("codex", "Codex", Readings.Healthy("codex", 20d));

        Assert.Equal("Session", claude.Metrics[0].Label);
        Assert.Equal(62d, claude.Headline?.Value);
        Assert.Equal("Session", claude.Headline?.WindowLabel);
        Assert.Equal("Session (Claude Code)", claude.Headline?.SourceLabel);

        Assert.Equal("Session", codex.Metrics[0].Label);
        Assert.Equal(38d, codex.Headline?.Value);
        Assert.Equal("Weekly", codex.Headline?.WindowLabel);
        Assert.Equal("Weekly (Codex)", codex.Headline?.SourceLabel);

        // Neither card is showing the highest reading on the machine, and the two dials are
        // carrying different windows - which is exactly when the name under each of them is
        // the only thing saying the two sweeps are not comparable.
        Assert.NotEqual(claude.Headline?.Value, codex.Headline?.Value);
        Assert.NotEqual(claude.Headline?.WindowLabel, codex.Headline?.WindowLabel);
    }

    /// <summary>
    /// The card's rule is the panel's rule. Both are
    /// <see cref="HeadlineReadingViewModel.Nearest"/>, run over a different set, so a tie break
    /// that changed on one surface could not stay unchanged on the other.
    /// </summary>
    [Fact]
    public void TheCardAndThePanelChooseByTheSameRule()
    {
        // Two windows at one level: the one that rolls over first takes the dial.
        var reading = new ProviderUsage(
            "claude",
            ProviderStatus.Active,
            [
                Readings.Metric("seven_day", "Weekly", 62d, TimeSpan.FromDays(7), Readings.Now.AddDays(3)),
                Readings.Metric("five_hour", "Session", 62d, TimeSpan.FromHours(5), Readings.Now.AddMinutes(20)),
            ],
            null,
            Readings.Now,
            null);

        using ProviderViewModel row = Row("claude", "Claude Code", reading);
        Assert.Equal("Session", row.Headline?.WindowLabel);

        // And the other way round, so the answer is the reset time rather than the order.
        var swapped = new ProviderUsage(
            "claude",
            ProviderStatus.Active,
            [reading.Metrics[1], reading.Metrics[0]],
            null,
            Readings.Now,
            null);

        using ProviderViewModel reversed = Row("claude", "Claude Code", swapped);
        Assert.Equal("Session", reversed.Headline?.WindowLabel);

        // The panel's answer over the same windows is the same window.
        using var panel = new PopupViewModel(
            [new FakeUsageProvider("claude", "Claude Code", reading)],
            new TestClock(Readings.Now),
            AltimSettings.Default);
        panel.Providers[0].Apply(reading);

        Assert.Equal(row.Headline?.WindowLabel, panel.Headline?.WindowLabel);
    }

    /// <summary>
    /// The window on the dial is not also a row beneath it. A card that drew one window twice
    /// would have a reader counting one more window than the provider reports.
    /// </summary>
    [AvaloniaFact]
    public void TheWindowOnTheDialIsNotRepeatedAsARowBeneathIt()
    {
        using ProviderViewModel row = Row("claude", "Claude Code", Readings.Healthy("claude"));

        Assert.Equal(2, row.Metrics.Count);
        MetricViewModel remaining = Assert.Single(row.RemainingMetrics);
        Assert.Equal("Weekly", remaining.Label);
        Assert.DoesNotContain(row.RemainingMetrics, m => m.Key == "five_hour");

        Surface.Show(new ProviderCardView { DataContext = row }, window =>
        {
            // One meter, for the one window that is not on the dial.
            Meter meter = Assert.Single(Surface.Visible<Meter>(window));
            Assert.Equal(38d, meter.Value);

            // And the head window's figure is on screen once, inside the face.
            Assert.Equal(1, Surface.Count(window, "62%"));
            Assert.Equal(1, Surface.Count(window, "38%"));
        }, width: 564d, height: 560d);
    }

    /// <summary>
    /// A reading that failed carries no metric, so the card has no dial and no rule where one
    /// would be: the sentence and the retry stand there instead. An instrument drawn over a
    /// reading that was never taken would be reporting on nothing.
    /// </summary>
    [AvaloniaFact]
    public void AFailedReadingLeavesNoDialOnTheCard()
    {
        using ProviderViewModel row = Row("claude", "Claude Code", Readings.Failed("claude"));

        Assert.False(row.HasHeadline);
        Assert.Null(row.Headline);
        Assert.Empty(row.RemainingMetrics);

        int rulesWhenFailed = 0;
        Surface.Show(new ProviderCardView { DataContext = row }, window =>
        {
            Assert.Empty(Surface.Visible<Dial>(window));
            Assert.True(Surface.Shows(window, UsageFormat.ProviderUnavailable));
            rulesWhenFailed = HorizontalRules(window);
        }, width: 564d, height: 560d);

        // The card's own rule above the footer is the one that was always there. The head's
        // rule went with the dial rather than standing over an empty space, which is the
        // difference this counts: two rules would leave a bare line across a failed card.
        using ProviderViewModel healthy = Row("claude", "Claude Code", Readings.Healthy("claude"));
        int rulesWhenHealthy = 0;
        Surface.Show(new ProviderCardView { DataContext = healthy }, window =>
        {
            Assert.Single(Surface.Visible<Dial>(window));
            rulesWhenHealthy = HorizontalRules(window);
        }, width: 564d, height: 560d);

        Assert.Equal(1, rulesWhenFailed);
        Assert.Equal(2, rulesWhenHealthy);
    }

    /// <summary>
    /// A head window the provider reports no figure for draws the unavailable face - an outline,
    /// no sweep - and an em dash. Unknown is not zero, and a sweep sitting at the bottom of the
    /// scale is the one picture that would read as a reported nothing.
    /// </summary>
    [AvaloniaFact]
    public void AnUnreportedHeadWindowIsNotDrawnAsAZero()
    {
        var silent = new ProviderUsage(
            "claude",
            ProviderStatus.Active,
            [
                Readings.Metric("five_hour", "Session", null, TimeSpan.FromHours(5), Readings.Now.AddMinutes(134)),
                Readings.Metric("seven_day", "Weekly", null, TimeSpan.FromDays(7), Readings.Now.AddDays(3)),
            ],
            null,
            Readings.Now,
            null);

        using ProviderViewModel row = Row("claude", "Claude Code", silent);

        Assert.Equal("Session", row.Headline?.WindowLabel);
        Assert.Null(row.Headline?.Value);
        Assert.Equal(UsageFormat.Unknown, row.Headline?.PercentText);

        Surface.Show(new ProviderCardView { DataContext = row }, window =>
        {
            Dial dial = Assert.Single(Surface.Visible<Dial>(window));

            Assert.Null(dial.Value);
            Assert.True(dial.IsUnavailable);
            Assert.Null(dial.RenderedSweep);
            Assert.DoesNotContain("0%", Surface.Lines(window));
            Assert.True(Surface.Shows(window, UsageFormat.Unknown));
        }, width: 564d, height: 560d);
    }

    /// <summary>
    /// The dial's accessible name says which provider it belongs to, and the reading is
    /// announced once. The figure standing inside the face is the dial's own reading, which the
    /// dial's peer already reads out, so it is out of the content view: without that a screen
    /// reader says the percentage twice on one card.
    /// </summary>
    [AvaloniaFact]
    public void TheDialNamesItsProviderAndTheReadingIsAnnouncedOnce()
    {
        using ProviderViewModel row = Row("claude", "Claude Code", Readings.Healthy("claude"));

        Surface.Show(new ProviderCardView { DataContext = row }, window =>
        {
            Dial dial = Assert.Single(Surface.Visible<Dial>(window));

            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(dial);
            Assert.Equal("Session (Claude Code), 62% used, threshold 80%", peer.GetName());

            TextBlock figure = Surface.Visible<TextBlock>(window).Single(t => t.Text == "62%");
            Assert.False(
                ControlAutomationPeer.CreatePeerForElement(figure).IsContentElement(),
                "The figure inside the face is announced as well as the dial that draws it.");

            // Across the whole card, the head window's level is offered to a reader once.
            List<string> announced = ContentNames(window);
            Assert.Equal(1, announced.Count(name => name.Contains("62%", StringComparison.Ordinal)));
        }, width: 564d, height: 560d);
    }

    /// <summary>
    /// The window's name stands in the 120 degrees the sweep leaves open at the foot of the
    /// face, not below the instrument. Under the face it would be 44 below the last ink of the
    /// dial it names and 16 above the first row beneath, which reads as a heading for the rows.
    /// </summary>
    [AvaloniaFact]
    public void TheWindowsNameStandsInTheArcsOwnOpening()
    {
        using ProviderViewModel row = Row("claude", "Claude Code", Readings.Healthy("claude"));
        var card = new ProviderCardView { DataContext = row };
        using PixelHost host = PixelHost.Show(card, width: 564d, height: 560d);

        Dial dial = Assert.Single(Surface.Visible<Dial>(host.Window));
        TextBlock name = Surface.Visible<TextBlock>(host.Window).Single(t => t.Text == "Session");

        Rect face = host.BoundsOf(dial);
        Rect label = host.BoundsOf(name);

        Assert.True(
            face.Contains(label.TopLeft) && face.Contains(label.BottomRight),
            $"The window's name at {label} is outside the face at {face}.");

        // Clear of the arc, which reaches its lowest at 120 degrees off the top: that is the
        // centre plus half the radius, so the opening is the bottom quarter of the face.
        double arcFoot = face.Center.Y + (face.Height / 4d);
        Assert.True(
            label.Y >= arcFoot,
            $"The name's top at {label.Y} is above the arc's foot at {arcFoot}.");
    }

    /// <summary>
    /// Past the threshold the window's name turns and the sweep does not, which is the meter's
    /// arrangement on the dial. Colour is never the only carrier: the index stands on the face
    /// at the level the threshold is set to, at twice a graduation's weight, whatever the name
    /// is coloured.
    /// </summary>
    [AvaloniaFact]
    public void PastTheThresholdTheNameTurnsAndTheSweepDoesNot()
    {
        var hot = new ProviderUsage(
            "claude",
            ProviderStatus.Active,
            [Readings.Metric("five_hour", "Session", 92d, TimeSpan.FromHours(5), Readings.Now.AddMinutes(20))],
            null,
            Readings.Now,
            null);

        using ProviderViewModel row = Row("claude", "Claude Code", hot);
        Assert.True(row.Headline?.IsAboveThreshold);

        Surface.Show(new ProviderCardView { DataContext = row }, window =>
        {
            Dial dial = Assert.Single(Surface.Visible<Dial>(window));
            Assert.True(dial.IsAboveThreshold);

            // The index is on the face because a threshold was reported, not because the
            // reading passed it: that is the mark a reader who cannot tell two colours apart
            // has to go on.
            Assert.Equal(AltimSettings.Default.SessionThresholdPercent, dial.Threshold);
            Assert.NotNull(dial.RenderedSweep);

            TextBlock name = Surface.Visible<TextBlock>(window).Single(t => t.Text == "Session");
            Assert.Contains("warn", name.Classes);

            // And a reading under the threshold leaves the same name in the ordinary ink.
            using ProviderViewModel calm = Row("claude", "Claude Code", Readings.Healthy("claude"));
            Assert.False(calm.Headline?.IsAboveThreshold);
        }, width: 564d, height: 560d);
    }

    /// <summary>
    /// Two cards side by side carry one instrument: the same face, the same scale, drawn at the
    /// same height in both cards. Different sizes would be two pictures rather than two
    /// readings, and a dial two rows further down its card would be compared with whatever
    /// happened to be beside it.
    /// </summary>
    [AvaloniaFact]
    public async Task TwoCardsSideBySideCarryOneInstrumentAtOneSize()
    {
        using DashboardViewModel dashboard = Dashboard();
        await dashboard.LoadAsync(TestContext.Current.CancellationToken);

        var window = new DashboardWindow(dashboard) { Width = 1180d, Height = 900d };

        Surface.Render(window, w =>
        {
            IReadOnlyList<Dial> dials = Surface.Visible<Dial>(w);
            Assert.Equal(2, dials.Count);

            Assert.Equal(Dial.Size, dials[0].Bounds.Width, 6);
            Assert.Equal(dials[0].Bounds.Size, dials[1].Bounds.Size);

            Point left = Origin(w, dials[0]);
            Point right = Origin(w, dials[1]);

            Assert.Equal(left.Y, right.Y, 6);
            Assert.True(right.X > left.X, "The two cards are not side by side.");

            // Same scale, so the same graduations: that is what makes two sweeps comparable.
            Assert.Equal(
                Dial.GraduationsFor(dials[0].Bounds.Width / 2d),
                Dial.GraduationsFor(dials[1].Bounds.Width / 2d));
            Assert.Equal(
                InstrumentScale.Graduations.Count,
                Dial.GraduationsFor(dials[0].Bounds.Width / 2d).Count);
        });
    }

    /// <summary>
    /// The two sweeps actually read differently, in pixels. Just past the top of the face the
    /// scale has passed half way, so a provider over 50 has ink there and one under it has
    /// track: the difference between 62 and 41 is visible without reading either figure, which
    /// is the whole claim being made for putting a dial on each card. The card's dial is
    /// banded too, so the ink there is the caution band's rather than the meter's fill: a card
    /// left on one colour while the panel's dial banded would be two instruments.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task TheTwoSweepsReadDifferentlyAtHalfWay(string variantName)
    {
        ThemeVariant variant = variantName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        using DashboardViewModel dashboard = Dashboard();
        await dashboard.LoadAsync(TestContext.Current.CancellationToken);

        var window = new DashboardWindow(dashboard)
        {
            Width = 1180d,
            Height = 900d,
            RequestedThemeVariant = variant,
        };

        using PixelHost host = PixelHost.Show(window);
        Frame frame = host.Capture();

        IReadOnlyList<Dial> dials = Surface.Visible<Dial>(window);
        Assert.Equal(2, dials.Count);

        Assert.Equal(62d, dials[0].Value);
        Assert.Equal(41d, dials[1].Value);

        Color caution = Token(variant, "AltimDialCautionBrush");
        Color normal = Token(variant, "AltimDialNormalBrush");
        Color track = Token(variant, "AltimMeterTrackBrush");

        // 55 rather than the top of the face itself: half way is where the caution band
        // begins, and a pixel sampled exactly on a boundary is a blend of the two colours
        // meeting there. The rail runs from the face's edge less the 4 deep engraving, the 2
        // gap and the 8 rail, so its middle is 62 in from a 72 radius.
        Point over = RailAt(host, dials[0], 55d);
        Point under = RailAt(host, dials[1], 55d);

        Assert.True(
            Ink.Near(frame.At(over), caution, 12),
            $"62% does not reach half way in the caution band: {frame.At(over)} at {over}.");
        Assert.True(
            Ink.Near(frame.At(under), track, 12),
            $"41% has swept past half way: {frame.At(under)} at {under}.");

        // And the same sweep is the normal band before it gets there, so the card's dial is
        // banded rather than repainted whole.
        Point early = RailAt(host, dials[0], 20d);
        Assert.True(
            Ink.Near(frame.At(early), normal, 12),
            $"62% is not in the normal band at a fifth: {frame.At(early)} at {early}.");

        // Said outright: at one place on two identical faces the two providers are a different
        // colour. That is the comparison the shared scale exists to make, and it is the claim
        // that would quietly stop being true if both dials ever ended up on one reading.
        Assert.NotEqual(frame.At(over), frame.At(under));
    }

    /// <summary>
    /// The grid still holds at the width the window refuses to go below: one column, a card of
    /// 564, a dial of the full 144 inside it with the whole tape, and the meters beneath it
    /// still at the 514 the design system measured them at.
    /// </summary>
    [AvaloniaFact]
    public async Task TheCardHoldsAtTheWindowsMinimumWidth()
    {
        using DashboardViewModel dashboard = Dashboard();
        await dashboard.LoadAsync(TestContext.Current.CancellationToken);

        var window = new DashboardWindow(dashboard) { Height = 720d };
        window.Width = window.MinWidth;

        Surface.Render(window, w =>
        {
            Assert.Equal(820d, window.MinWidth);

            IReadOnlyList<ProviderCardView> cards = Surface.Visible<ProviderCardView>(w);
            Assert.Equal(2, cards.Count);

            // One column: the two cards are stacked, not beside one another.
            Assert.Equal(Origin(w, cards[0]).X, Origin(w, cards[1]).X, 6);
            Assert.Equal(564d, cards[0].Bounds.Width, 6);

            IReadOnlyList<Dial> dials = Surface.Visible<Dial>(w);
            Assert.Equal(2, dials.Count);

            foreach (Dial dial in dials)
            {
                Assert.Equal(Dial.Size, dial.Bounds.Width, 6);
                Assert.Equal(Dial.Size, dial.Bounds.Height, 6);

                // The whole tape, fine band included, on the card's own face.
                Assert.Equal(
                    InstrumentScale.Graduations.Count,
                    Dial.GraduationsFor(dial.Bounds.Width / 2d).Count);
            }

            // And the rows beneath are still the widest meters the window can produce.
            IReadOnlyList<Meter> meters = Surface.Visible<Meter>(w);
            Assert.NotEmpty(meters);
            Assert.Equal(514d, meters.Min(m => m.Bounds.Width), 6);
        });
    }

    /// <summary>The full width rules on a card, which is every separator but the footer's.</summary>
    private static int HorizontalRules(Visual root) =>
        Surface.Visible<Separator>(root).Count(rule => rule.Bounds.Width > 1d);

    private static ProviderViewModel Row(string id, string name, ProviderUsage reading)
    {
        var provider = new FakeUsageProvider(id, name, reading);
        var row = new ProviderViewModel(provider, new TestClock(Readings.Now), AltimSettings.Default);
        row.Apply(reading);
        return row;
    }

    private static DashboardViewModel Dashboard()
    {
        IUsageProvider[] providers =
        [
            new FakeUsageProvider("claude", "Claude Code", Readings.Healthy("claude")),
            new FakeUsageProvider("codex", "Codex", Readings.Healthy("codex", 41d)),
        ];

        return new DashboardViewModel(
            providers,
            new FakeHistoryService(),
            new FakeSettingsStore(),
            new FakeStatusLineService(),
            new TestClock(Readings.Now));
    }

    private static Point Origin(Visual root, Visual visual)
    {
        Point? origin = visual.TranslatePoint(default, root);
        Assert.True(origin is not null, "The visual is not in this window.");
        return origin!.Value;
    }

    /// <summary>The middle of the rail at the top of a dial, which is half way up the scale.</summary>
    private static Point RailAt(PixelHost host, Dial dial, double level)
    {
        Rect face = host.BoundsOf(dial);
        double rail = (face.Width / 2d)
            - InstrumentScale.ScaleDepth
            - InstrumentScale.ScaleGap
            - (InstrumentScale.RailThickness / 2d);
        double radians = Dial.AngleFor(level) * Math.PI / 180d;

        return new Point(
            face.Center.X + (rail * Math.Sin(radians)),
            face.Center.Y - (rail * Math.Cos(radians)));
    }

    /// <summary>Every name an assistive technology is offered below a root, in tree order.</summary>
    private static List<string> ContentNames(Visual root)
    {
        List<string> names = [];
        Collect(ControlAutomationPeer.CreatePeerForElement((Control)root), names);
        return names;

        static void Collect(AutomationPeer peer, List<string> names)
        {
            if (peer.IsContentElement() && peer.GetName() is { Length: > 0 } name)
            {
                names.Add(name);
            }

            foreach (AutomationPeer child in peer.GetChildren())
            {
                Collect(child, names);
            }
        }
    }

    private static Color Token(ThemeVariant variant, string key)
    {
        Application app = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(app.Resources.TryGetResource(key, variant, out object? value), $"{key} does not resolve.");
        return Assert.IsType<SolidColorBrush>(value).Color;
    }
}
