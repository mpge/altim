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
/// because the design system named the accents and supplied no artwork, and then with marks
/// drawn from memory, which passed every assertion here and still looked wrong. They now
/// carry each vendor's own outline, traced by the Simple Icons set from the vendor's own
/// brand asset and moved onto the box by scale and translation alone. That is nominative
/// use: see <c>docs/DESIGN.md</c>.
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
    /// Neither vendor mark is the placeholder it replaced, and neither is the handful of
    /// straight segments a placeholder is: the burst is one long outline and the knot is
    /// eight figures, which is how the vendors' own artwork is built.
    /// </summary>
    /// <remarks>
    /// The figure counts are the shape of the real marks, not of a drawing of them. The
    /// burst is a single closed outline that travels out along one flank of each spoke and
    /// back down the other - a mark redrawn as one figure per spoke would be nine, which is
    /// what the drawn placeholder this replaced was. The knot is a silhouette plus the seven
    /// counters cut out of it. Both are held to a segment count no placeholder can reach, so
    /// a path that collapsed back to a few straight lines cannot pass this quietly.
    /// </remarks>
    [Fact]
    public void NeitherMarkIsThePlaceholderItReplaced()
    {
        string claude = ProviderIdentity.GlyphPath(ProviderIdentity.ClaudeId);
        string codex = ProviderIdentity.GlyphPath(ProviderIdentity.CodexId);

        Assert.NotEqual(PlaceholderDiamond, claude);
        Assert.NotEqual(PlaceholderHexagon, codex);

        Assert.Equal(1, Figures(claude));
        Assert.True(Figures(codex) >= 6, $"The knot is {Figures(codex)} figures, not a knot.");

        Assert.True(Segments(claude) >= 100, $"The burst is {Segments(claude)} segments, not a burst.");
        Assert.True(Segments(codex) >= 50, $"The knot is {Segments(codex)} segments, not a knot.");

        // Both open with the non-zero fill rule, which is the rule SVG applies when a file
        // names none and therefore the rule the vendors' own files are drawn under.
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
    /// The burst is solid where its spokes meet and the knot is open at its centre, which is
    /// what keeps the two apart at 16px - and what balances an OpenAI accent that is very
    /// nearly the ink colour against an Anthropic accent that is not.
    /// </summary>
    /// <param name="size">The glyph size, in device independent pixels.</param>
    /// <remarks>
    /// One box at the middle of the mark, an eighth of it across, read twice. The knot's
    /// counter is the hexagon in OpenAI's own artwork, a little over a fifth of the mark
    /// wide; the old drawn mark's hollow was half the box wide, so a probe sized for that one
    /// runs over the strands here and reads ink. This is the largest box the real counter
    /// holds at both sizes, and the burst fills the same box solid.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(16)]
    [InlineData(28)]
    public void TheBurstIsSolidAtItsCentreAndTheKnotIsOpen(int size)
    {
        var centre = new Rect(size * 7d / 16d, size * 7d / 16d, size / 8d, size / 8d);

        using (PixelHost claude = Draw(ProviderIdentity.ClaudeId, size))
        {
            Frame frame = claude.Capture();
            Assert.True(
                frame.Count(centre, Ink.IsInk) == frame.Count(centre, _ => true),
                $"The burst is not solid where its spokes meet at {size}px: {frame.Describe(centre)}");
        }

        using PixelHost codex = Draw(ProviderIdentity.CodexId, size);
        Frame knot = codex.Capture();
        Assert.True(
            knot.Count(centre, Painted) == 0,
            $"The knot's centre is filled at {size}px: {knot.Describe(centre)}");
    }

    /// <summary>
    /// The knot's counters are holes, its strands are filled, and the fill rule it declares
    /// is not what decides either: the vendors' artwork reads the same under both rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces an assertion that the leading <c>F1</c> was load bearing, which was true
    /// of the drawn mark it guarded - six bars lapping at the corners, every lap holed out by
    /// even-odd - and is not true of OpenAI's own. The real knot is a silhouette with seven
    /// counters nested inside it and no two subpaths overlapping anywhere, so both rules draw
    /// it identically. Asserting that is worth as much as the old assertion was: it is the
    /// property that makes <c>F1</c> safe to declare, and the property that a re-trace from a
    /// newer vendor asset could silently lose.
    /// </para>
    /// <para>
    /// The rules are compared by geometry rather than by pixels, over a grid fine enough to
    /// fall inside every counter the mark has, because a difference the rasteriser rounds
    /// away at 16px is still a difference in the mark.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void TheKnotsCountersAreHolesWhicheverFillRuleIsApplied()
    {
        string data = ProviderIdentity.GlyphPath(ProviderIdentity.CodexId);
        Geometry knot = Geometry.Parse(data);
        Geometry withoutTheRule = Geometry.Parse(data.Replace("F1 ", string.Empty, StringComparison.Ordinal));

        // The hexagon at the centre of the mark, and one point on each side of the braid.
        Assert.False(knot.FillContains(new Point(8d, 8d)), "The knot's centre counter is filled in.");
        foreach (Point strand in new[]
        {
            new Point(7.5d, 1d), new Point(1d, 7d), new Point(8.5d, 15d), new Point(15d, 9d),
        })
        {
            Assert.True(knot.FillContains(strand), $"The knot has a hole at {strand}.");
        }

        int disagreements = 0;
        for (int x = 0; x <= 160; x++)
        {
            for (int y = 0; y <= 160; y++)
            {
                var point = new Point(x / 10d, y / 10d);
                if (knot.FillContains(point) != withoutTheRule.FillContains(point))
                {
                    disagreements++;
                }
            }
        }

        Assert.True(
            disagreements == 0,
            $"Even-odd draws a different knot at {disagreements} points, so the mark now depends "
                + "on the rule rather than merely declaring the vendor's own.");
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

    /// <summary>
    /// How many drawing commands a path is made of, the leading fill rule aside.
    /// </summary>
    /// <param name="data">The path data.</param>
    private static int Segments(string data) =>
        data[(data.IndexOf(' ', StringComparison.Ordinal) + 1)..].Count(char.IsLetter);
}
