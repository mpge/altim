using Altim.UI.Controls;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The tape's one uncompromisable rule, read off the screen: a sample the provider never
/// reported and a sample reported as nothing are not the same picture.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UsageTapeSeries.Values"/> says it in its own documentation - "a null is a gap
/// the line breaks across, never a zero: a sample the provider did not report is not a sample
/// of nothing" - and until this file existed nothing held it. The test that claimed to,
/// <see cref="UsageTapeTests.RendersASeriesWithGaps"/>, only asked that the tape look unlike
/// an empty one, which a tape plotting every null at the foot of the scale satisfies just as
/// comfortably. A tape that reads an unreported sample as a zero draws a line diving to the
/// bottom of the plot and back, and that is a claim about a level nobody measured.
/// </para>
/// <para>
/// Every assertion here was run against a tape whose nulls really were zeros, and every one
/// of them failed. The probe is anchored on both sides for the same reason: the frame with
/// the gaps must carry no line low in the plot, and the frame with the zeros must carry one,
/// so a probe that had stopped being able to see a line cannot report a pass.
/// </para>
/// <para>
/// Frames come through <see cref="PixelHost"/>, which copies the pixels out and disposes the
/// platform bitmap before returning: an undisposed frame takes the renderer down.
/// </para>
/// </remarks>
public sealed class UsageTapePixelTests
{
    /// <summary>The window width every test here lays a tape out in.</summary>
    /// <remarks>
    /// 360 by 140 leaves the plot 126 device pixels tall with a 7 pixel pad above and below,
    /// so the three level rules land on rows 7, 70 and 133 and nothing being measured is
    /// within the rounding of an edge.
    /// </remarks>
    private const double Width = 360d;

    /// <summary>The window height. See <see cref="Width"/>.</summary>
    private const double Height = 140d;

    /// <summary>
    /// How far from the ground a pixel has to be before it can only be the series line.
    /// </summary>
    /// <remarks>
    /// The tape's palette is four tokens deep and they stand a long way apart from the ground
    /// they are drawn on: the level rules sit 21 away in Light and 24 in Dark, the label and
    /// the inline series name 153 and 128, and the primary line 245 and 230. A threshold of
    /// 200 therefore reads the line and nothing else, in both variants, with the nearest other
    /// token 47 away on one side and 45 on the other. Matching a colour rather than a distance
    /// would not survive antialiasing: a half covered line pixel is a grey that matches no
    /// token at all.
    /// </remarks>
    private const double LineInk = 200d;

    /// <summary>
    /// How far from the ground a pixel has to be to count as part of a drawn mark.
    /// </summary>
    /// <remarks>
    /// Lower than <see cref="LineInk"/>, because this one measures how deep a mark is and the
    /// outermost row of a mark is always partly covered. It is still well clear of the level
    /// rules, which are the only other thing inside the plot once the level labels and the
    /// inline names are turned off.
    /// </remarks>
    private const double BandInk = 60d;

    /// <summary>Both variants, so a line that only exists in Light fails here.</summary>
    public static TheoryData<string> Variants => ["Light", "Dark"];

    /// <summary>
    /// The assertion the series' own documentation names. A run with a hole in it and the same
    /// run with a zero in the hole are different pictures, and the difference is that only one
    /// of them draws a line at the bottom of the plot.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    /// <remarks>
    /// Both readings are taken from the lower half of the plot, which is where a zero is and
    /// where a run of hundreds never goes. The series is five samples at 100 with the middle
    /// one unreported, so a tape that honours the null draws two flat segments along the top
    /// and nothing else, and a tape that reads the null as a zero draws a V reaching the floor.
    /// </remarks>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AGapIsNotDrawnAsAZero(string name)
    {
        ThemeVariant variant = Variant(name);
        Color ground = Token(variant, "AltimSurfaceBrush");

        UsageTape gaps = Bare([100d, 100d, null, 100d, 100d]);
        UsageTape zeroes = Bare([100d, 100d, 0d, 100d, 100d]);

        Frame withGaps;
        Rect bounds;
        using (PixelHost host = PixelHost.Show(gaps, variant, Width, Height))
        {
            withGaps = host.Capture();
            bounds = host.BoundsOf(gaps);
        }

        Frame withZeroes;
        using (PixelHost host = PixelHost.Show(zeroes, variant, Width, Height))
        {
            withZeroes = host.Capture();
            Assert.Equal(bounds, host.BoundsOf(zeroes));
        }

        var upper = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height / 2d);
        var lower = new Rect(bounds.X, bounds.Y + (bounds.Height / 2d), bounds.Width, bounds.Height / 2d);

        // The probe can see a line when there is one to see. Without these two the assertion
        // below is held up by a reading of zero that a blank frame would produce just as well.
        Assert.True(
            LineInkIn(withZeroes, lower, ground) > 50,
            "The zero filled tape drew no line in the lower half of the plot, so this probe "
                + $"cannot tell the two apart: {withZeroes.Describe(lower)}.");
        Assert.True(
            LineInkIn(withGaps, upper, ground) > 50,
            $"The tape with gaps drew no line at all: {withGaps.Describe(upper)}.");

        Assert.True(
            LineInkIn(withGaps, lower, ground) == 0,
            "An unreported sample was drawn as a zero: the tape put a line in the lower half of "
                + $"the plot, where a run of hundreds never goes: {withGaps.Describe(lower)}.");

        // And the whole picture differs, which is the same claim with no probe in it.
        Assert.True(
            withGaps.DifferenceWith(withZeroes, bounds) > 0,
            "A run with a hole in it and the same run with a zero in the hole painted the same "
                + $"picture inside {bounds}: {withGaps.Describe(bounds)}.");
    }

    /// <summary>
    /// The same rule at the ends of a run, which is where it is easiest to lose: a leading null
    /// decides where the line starts, and a trailing one decides where it stops and where the
    /// provider's name is set.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    /// <remarks>
    /// The runs are longer than <see cref="UsageTape.DotSampleLimit"/> on purpose, so that the
    /// line is the only thing left that can differ. Below the limit the tape marks each
    /// reported sample with a dot, so a run with a hole in it has one dot fewer than the same
    /// run with a zero in the hole - and the two pictures then differ whatever the line does.
    /// A tape that plotted the hole at the foot of the scale passed the short version of this
    /// assertion, held up by the missing dot alone.
    /// </remarks>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AGapAtEitherEndIsNotDrawnAsAZero(string name)
    {
        ThemeVariant variant = Variant(name);

        foreach ((string Where, double?[] Gapped, double?[] Zeroed) shape in
            new (string, double?[], double?[])[]
            {
                ("leading", Undotted(0, null), Undotted(0, 0d)),
                ("trailing", Undotted(UsageTape.DotSampleLimit, null), Undotted(UsageTape.DotSampleLimit, 0d)),
            })
        {
            UsageTape gapped = Bare(shape.Gapped);

            Frame withGap;
            Rect bounds;
            using (PixelHost host = PixelHost.Show(gapped, variant, Width, Height))
            {
                withGap = host.Capture();
                bounds = host.BoundsOf(gapped);
            }

            UsageTape zeroed = Bare(shape.Zeroed);
            using PixelHost second = PixelHost.Show(zeroed, variant, Width, Height);
            Frame withZero = second.Capture();

            Assert.Equal(bounds, second.BoundsOf(zeroed));
            Assert.True(
                withGap.DifferenceWith(withZero, bounds) > 0,
                $"A {shape.Where} null was drawn as a zero: the two tapes painted the same "
                    + $"picture inside {bounds}: {withGap.Describe(bounds)}.");
        }
    }

    /// <summary>
    /// The empty state is a sentence. Not a plot of nothing, and not a flat line along the
    /// floor, which is the gap rule's own lie told at the scale of a whole history.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    /// <remarks>
    /// Three readings, because no one of them says it alone. There is ink, so something was
    /// written. None of it is line ink, so no series was drawn. And no row carries a long
    /// unbroken run, which is what a level rule is: a drawn plot rules the full width of itself
    /// three times, and a sentence's longest run is one letter wide.
    /// </remarks>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheEmptyStateIsASentenceAndNoLine(string name)
    {
        ThemeVariant variant = Variant(name);
        Color ground = Token(variant, "AltimSurfaceBrush");

        var tape = new UsageTape();
        using PixelHost host = PixelHost.Show(tape, variant, Width, Height);
        Frame frame = host.Capture();
        Rect bounds = host.BoundsOf(tape);

        Assert.True(
            frame.Count(bounds, colour => Distance(colour, ground) > 1d) > 0,
            $"The empty state wrote nothing at all: {frame.Describe(bounds)}.");
        Assert.True(
            LineInkIn(frame, bounds, ground) == 0,
            $"The empty state drew a series line rather than a sentence: {frame.Describe(bounds)}.");

        int longest = LongestRunIn(frame, bounds, ground);
        Assert.True(
            longest < bounds.Width / 4d,
            $"The empty state drew a rule {longest} pixels long, so it is a plot of nothing "
                + "rather than a sentence.");
    }

    /// <summary>
    /// The sentence on screen is the one the control was given, so a tape that painted a fixed
    /// line of text of its own would not pass.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheEmptySentenceIsTheOneTheTapeWasGiven(string name) =>
        DesignSystem.AssertRendersUnlike(
            new UsageTape(),
            new UsageTape { EmptyText = "Nothing has been recorded." },
            Variant(name),
            Width,
            Height);

    /// <summary>
    /// A sample below the dot limit is marked where it stands. Read as depth rather than as a
    /// colour: the dot is drawn in the line's own brush, so the only thing telling the two
    /// apart is that the mark at a sample reaches further than the line either side of it.
    /// </summary>
    /// <param name="name">The theme variant to render under.</param>
    /// <remarks>
    /// The series is flat, so the polyline is one horizontal band of constant depth and every
    /// extra row is a dot. The samples at the two ends are left out: they stand on the plot's
    /// own edges, where half of the dot is outside the frame and the reading is not comparable
    /// with one taken in the middle.
    /// </remarks>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ASampleBelowTheDotLimitIsMarkedWhereItStands(string name)
    {
        ThemeVariant variant = Variant(name);
        Color ground = Token(variant, "AltimSurfaceBrush");

        const int Count = 5;
        UsageTape tape = Bare([50d, 50d, 50d, 50d, 50d]);
        Assert.True(Count < UsageTape.DotSampleLimit, "This series is not below the dot limit.");

        using PixelHost host = PixelHost.Show(tape, variant, Width, Height);
        Frame frame = host.Capture();
        Rect bounds = host.BoundsOf(tape);

        for (int index = 1; index < Count - 1; index++)
        {
            double here = UsageTape.XFor(index, Count, bounds.Width);
            double next = UsageTape.XFor(index + 1, Count, bounds.Width);
            int onSample = (int)Math.Round(bounds.X + here);
            int between = (int)Math.Round(bounds.X + ((here + next) / 2d));

            int marked = BandDepthAt(frame, bounds, onSample, ground);
            int plain = BandDepthAt(frame, bounds, between, ground);

            Assert.True(
                plain > 0,
                $"There is no line between samples {index} and {index + 1}, at x={between}, so "
                    + "this reading cannot tell a dot from a line.");
            Assert.True(
                marked >= plain + 2,
                $"Sample {index} at x={onSample} is {marked} rows deep and the line between "
                    + $"samples is {plain}, so no dot was drawn on it.");
        }
    }

    /// <summary>
    /// A tape with nothing but the plot in it: no level labels, no inline series names and no
    /// date axis, so the plot is the whole control and the only things inside it are the three
    /// level rules and the series itself.
    /// </summary>
    /// <param name="values">The samples, oldest first.</param>
    private static UsageTape Bare(IReadOnlyList<double?> values) => new()
    {
        ShowLevelLabels = false,
        ShowSeriesNames = false,
        Series = [new UsageTapeSeries("Claude", values)],
    };

    /// <summary>
    /// A flat run at 80 too long for sample dots, with one sample replaced.
    /// </summary>
    /// <param name="index">The sample to replace.</param>
    /// <param name="level">What stands there: null for a sample the provider never reported.</param>
    /// <returns>The samples, oldest first.</returns>
    private static double?[] Undotted(int index, double? level)
    {
        var values = new double?[UsageTape.DotSampleLimit + 1];
        Array.Fill(values, 80d);
        values[index] = level;
        return values;
    }

    /// <summary>How many pixels inside an area can only be the series line.</summary>
    private static int LineInkIn(Frame frame, Rect area, Color ground) =>
        frame.Count(area, colour => Distance(colour, ground) > LineInk);

    /// <summary>How many rows of one column carry part of a drawn mark.</summary>
    private static int BandDepthAt(Frame frame, Rect bounds, int x, Color ground) =>
        frame.Count(
            new Rect(x, bounds.Y, 1d, bounds.Height),
            colour => Distance(colour, ground) > BandInk);

    /// <summary>The longest unbroken run of anything but the ground, along any one row.</summary>
    private static int LongestRunIn(Frame frame, Rect bounds, Color ground)
    {
        int longest = 0;
        for (int y = (int)bounds.Y; y < (int)bounds.Bottom; y++)
        {
            int run = 0;
            for (int x = (int)bounds.X; x < (int)bounds.Right; x++)
            {
                run = Distance(frame.At(x, y), ground) > 1d ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }

        return longest;
    }

    /// <summary>The mean per channel distance between two colours.</summary>
    private static double Distance(Color colour, Color from) =>
        (Math.Abs(colour.R - from.R) + Math.Abs(colour.G - from.G) + Math.Abs(colour.B - from.B)) / 3d;

    private static ThemeVariant Variant(string name) =>
        name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static Color Token(ThemeVariant variant, string key)
    {
        Assert.True(
            DesignSystem.Ensure().TryGetResource(key, variant, out object? value),
            $"{key} does not resolve under {variant}.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
