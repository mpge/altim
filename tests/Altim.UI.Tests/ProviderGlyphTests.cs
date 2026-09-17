using Altim.UI.Formatting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Altim.UI.Tests;

/// <summary>
/// The provider marks, held to the box they are drawn on and to the pixels they reach.
/// </summary>
/// <remarks>
/// <para>
/// Both providers used to be drawn with placeholder geometry - a diamond and a hexagon -
/// because the design system named the accents and supplied no artwork. They now carry each
/// vendor's own mark, which is nominative use: see <c>docs/DESIGN.md</c>.
/// </para>
/// <para>
/// Path data is easy to assert nothing about. A mark that parsed, sat in the box and drew a
/// single dot would satisfy every property a test can read off the object graph, so the
/// assertions that matter here are read off the screen: ink in every quadrant, at every size
/// a view asks for. Every frame is captured through <see cref="Frame"/>, which copies the
/// pixels out and disposes the platform bitmap before returning - an undisposed frame takes
/// the renderer down on Linux.
/// </para>
/// </remarks>
public sealed class ProviderGlyphTests
{
    /// <summary>The placeholder marks these replaced, so a revert cannot pass quietly.</summary>
    private const string PlaceholderDiamond = "M 8,0.5 L 15.5,8 L 8,15.5 L 0.5,8 Z";

    private const string PlaceholderHexagon =
        "M 8,0.5 L 14.5,4.25 L 14.5,11.75 L 8,15.5 L 1.5,11.75 L 1.5,4.25 Z";

    /// <summary>Every provider identifier a view can hand the glyph, marked or not.</summary>
    public static TheoryData<string> Marks =>
    [
        ProviderIdentity.ClaudeId,
        ProviderIdentity.CodexId,
        "gemini",
    ];

    /// <summary>
    /// Each identifier at each size the views use: 16 beside a label, 20 in an activity row,
    /// 28 where a provider heads a card.
    /// </summary>
    public static TheoryData<string, int> MarksAtEverySize
    {
        get
        {
            TheoryData<string, int> data = [];
            foreach (string id in new[] { ProviderIdentity.ClaudeId, ProviderIdentity.CodexId, "gemini" })
            {
                foreach (int size in new[] { 16, 20, 28 })
                {
                    data.Add(id, size);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// The two vendors and the fallback wear three different marks, and the mark a provider
    /// gets is chosen by its identifier whatever case it arrives in.
    /// </summary>
    [Fact]
    public void EachProviderWearsAMarkOfItsOwn()
    {
        string claude = ProviderIdentity.GlyphPath(ProviderIdentity.ClaudeId);
        string codex = ProviderIdentity.GlyphPath(ProviderIdentity.CodexId);
        string other = ProviderIdentity.GlyphPath("gemini");

        Assert.NotEqual(claude, codex);
        Assert.NotEqual(claude, other);
        Assert.NotEqual(codex, other);

        Assert.Equal(claude, ProviderIdentity.GlyphPath("Claude"));
        Assert.Equal(codex, ProviderIdentity.GlyphPath("CODEX"));
        Assert.Equal(other, ProviderIdentity.GlyphPath(null));
    }

    /// <summary>
    /// Neither vendor mark is the placeholder it replaced, and neither is a single closed
    /// outline: a burst of spokes and a knot of lapped bars are both several figures.
    /// </summary>
    [Fact]
    public void NeitherMarkIsThePlaceholderItReplaced()
    {
        string claude = ProviderIdentity.GlyphPath(ProviderIdentity.ClaudeId);
        string codex = ProviderIdentity.GlyphPath(ProviderIdentity.CodexId);

        Assert.NotEqual(PlaceholderDiamond, claude);
        Assert.NotEqual(PlaceholderHexagon, codex);

        Assert.True(Figures(claude) >= 6, $"The burst is {Figures(claude)} figures, not a burst.");
        Assert.True(Figures(codex) >= 6, $"The knot is {Figures(codex)} figures, not a knot.");

        // Both open with the non-zero fill rule. Even-odd holes out every lap and every
        // overlap, which is a silent change: the path still parses and still draws.
        Assert.StartsWith("F1 ", claude, StringComparison.Ordinal);
        Assert.StartsWith("F1 ", codex, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every mark fills the 16x16 box on all four sides. A shape stretched uniformly is
    /// scaled to its own bounds and pinned to the top left of its slot rather than centred,
    /// so a mark whose bounds are narrower than they are tall - the old hexagon was 13 wide
    /// in a 15 tall box - hangs to one side of the label it belongs to.
    /// </summary>
    /// <param name="providerId">The provider whose mark to measure.</param>
    [AvaloniaTheory]
    [MemberData(nameof(Marks))]
    public void EveryMarkFillsTheSixteenPixelBoxOnAllFourSides(string providerId)
    {
        Rect bounds = Geometry.Parse(ProviderIdentity.GlyphPath(providerId)).Bounds;

        Assert.InRange(bounds.X, 0d, 16d);
        Assert.InRange(bounds.Y, 0d, 16d);
        Assert.InRange(bounds.Right, 0d, 16d);
        Assert.InRange(bounds.Bottom, 0d, 16d);

        Assert.Equal(0.5d, bounds.X, 2);
        Assert.Equal(0.5d, bounds.Y, 2);
        Assert.Equal(15.5d, bounds.Right, 2);
        Assert.Equal(15.5d, bounds.Bottom, 2);
    }

    /// <summary>
    /// Every mark reaches ink into all four quadrants, out past the middle of the box, at
    /// every size a view asks for. This is the assertion a mark that collapsed to a dot -
    /// or to one stray spoke, or to a shape the parser silently truncated - cannot pass.
    /// </summary>
    /// <param name="providerId">The provider whose mark to draw.</param>
    /// <param name="size">The glyph size, in device independent pixels.</param>
    [AvaloniaTheory]
    [MemberData(nameof(MarksAtEverySize))]
    public void EveryMarkPaintsInEveryQuadrant(string providerId, int size)
    {
        using PixelHost host = Draw(providerId, size);
        Frame frame = host.Capture();

        double centre = size / 2d;
        double clear = size * 0.28d;

        foreach ((int Right, int Down) quadrant in new[] { (1, -1), (1, 1), (-1, 1), (-1, -1) })
        {
            int ink = 0;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double dx = x + 0.5d - centre;
                    double dy = y + 0.5d - centre;

                    if (Math.Sign(dx) != quadrant.Right || Math.Sign(dy) != quadrant.Down)
                    {
                        continue;
                    }

                    if (Math.Sqrt((dx * dx) + (dy * dy)) < clear || !Painted(frame.At(x, y)))
                    {
                        continue;
                    }

                    ink++;
                }
            }

            Assert.True(
                ink >= 8,
                $"{providerId} at {size}px painted {ink} pixels in the "
                    + $"({quadrant.Right}, {quadrant.Down}) quadrant, clear of the middle: "
                    + frame.Describe(new Rect(0d, 0d, size, size), take: 3));
        }
    }

    /// <summary>
    /// The burst is solid where its spokes meet and the knot is hollow, which is what keeps
    /// the two apart at 16px - and what balances an OpenAI accent that is very nearly the
    /// ink colour against an Anthropic accent that is not.
    /// </summary>
    [AvaloniaFact]
    public void TheBurstIsSolidAtItsCentreAndTheKnotIsHollow()
    {
        var middle = new Rect(6d, 6d, 4d, 4d);

        using (PixelHost claude = Draw(ProviderIdentity.ClaudeId, 16))
        {
            Frame frame = claude.Capture();
            Assert.True(
                frame.Count(middle, Ink.IsInk) >= 12,
                $"The burst is hollow where its spokes meet: {frame.Describe(middle)}");
        }

        using PixelHost codex = Draw(ProviderIdentity.CodexId, 16);
        Frame knot = codex.Capture();
        Assert.True(
            knot.Count(middle, Painted) == 0,
            $"The knot is filled where it should be open: {knot.Describe(middle)}");
    }

    /// <summary>
    /// The knot's laps are filled rather than holed out. Its six bars run past one another
    /// at the corners, and under the default even-odd rule each overlap cancels itself into
    /// a hole - which the leading <c>F1</c> in the path data is there to prevent.
    /// </summary>
    [AvaloniaFact]
    public void TheKnotsLapsAreFilledRatherThanHoledOut()
    {
        string data = ProviderIdentity.GlyphPath(ProviderIdentity.CodexId);
        Geometry knot = Geometry.Parse(data);
        Geometry withoutTheRule = Geometry.Parse(data.Replace("F1 ", string.Empty, StringComparison.Ordinal));

        // Inside the lap at the top corner, where two bars cross.
        var lap = new Point(8.25d, 2d);

        Assert.True(knot.FillContains(lap), "The knot has a hole at its top corner.");
        Assert.False(
            withoutTheRule.FillContains(lap),
            "Even-odd no longer holes the lap out, so the F1 this guards is not load bearing.");
    }

    /// <summary>
    /// Whether a pixel carries paint rather than ground.
    /// </summary>
    /// <param name="colour">The pixel to judge.</param>
    /// <remarks>
    /// A quarter coverage rather than <see cref="Ink.IsInk"/>'s near-solid one. The burst's
    /// spokes are a fraction of a pixel wide near their tips at 16px, so full strength ink is
    /// the wrong bar for them: a spoke that the rasteriser spread over two columns at partial
    /// coverage is still a spoke, and holding it to solid ink would make this test's verdict
    /// depend on where the spoke happened to land rather than on whether it was drawn.
    /// </remarks>
    private static bool Painted(Color colour) =>
        colour.A > 0x40 && colour.R < 0xC0 && colour.G < 0xC0 && colour.B < 0xC0;

    /// <summary>Puts one mark on a white ground at one size, filled in ink.</summary>
    /// <param name="providerId">The provider whose mark to draw.</param>
    /// <param name="size">The glyph size, which is also the window size.</param>
    /// <returns>The open host, whose window the caller closes.</returns>
    private static PixelHost Draw(string providerId, int size)
    {
        var glyph = new ShapePath
        {
            Data = Geometry.Parse(ProviderIdentity.GlyphPath(providerId)),
            Fill = Brushes.Black,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        return PixelHost.Show(
            new Border { Background = Brushes.White, Child = glyph },
            width: size,
            height: size);
    }

    /// <summary>How many closed figures a path is made of.</summary>
    /// <param name="data">The path data.</param>
    private static int Figures(string data) => data.Count(character => character == 'M');
}
