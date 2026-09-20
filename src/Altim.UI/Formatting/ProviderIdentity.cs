using Avalonia.Media;

namespace Altim.UI.Formatting;

/// <summary>
/// The mark and the accent that carry a provider's identity.
/// </summary>
/// <remarks>
/// <para>
/// Each of the three providers is drawn as its own vendor's mark: Anthropic's radial burst
/// for Claude, OpenAI's interlocking knot for Codex, Google's four pointed star for Gemini.
/// Anything else wears the neutral circle. Every mark is a single monochrome path on the 16px
/// box, filled in the provider's accent, because the accent is what carries identity - a mark
/// drawn in its vendor's own colours would repeat what the palette already says, in colours
/// the palette does not hold. The views stretch that box to 16, 20 or 28 depending on what
/// the mark is standing beside.
/// </para>
/// <para>
/// <strong>The path data is the vendors' own outlines, not a drawing of them.</strong> All
/// three were traced by the Simple Icons set from the vendor's own published brand asset, and
/// all three were moved onto this box by scale and translation alone, so the outlines are
/// still the vendors' to four decimal places. A mark drawn from memory is a different mark
/// that resembles one - which is exactly what the first two replaced.
/// </para>
/// <para>
/// Two of the three are in that set today and carry no per-icon licence of their own, so the
/// project's CC0 waiver is what applies to them. <strong>The OpenAI mark is not in the set any
/// more.</strong> It was added in July 2020 and removed in November 2025, in
/// simple-icons#13944, because nobody obtained OpenAI's permission to keep it: the maintainers
/// raised the removal in advance and asked contributors to seek permission, and none did.
/// Altim's path was traced from it while it was there. That changes what can honestly be
/// claimed about provenance and changes nothing about the legal footing, because the CC0
/// waiver never covered OpenAI's trademark - only Simple Icons' own tracing work. What Altim
/// relies on is nominative use, the same thing it relies on for the vendors' names.
/// </para>
/// <para>
/// These marks identify the vendors' own products, which is nominative use. Altim claims no
/// endorsement by, or affiliation with, Anthropic, OpenAI or Google. No mark is restyled or
/// combined with Altim's own, and none stands for Altim. Each is filled in a per-provider
/// accent rather than the interface's foreground, which is a recolour and is said plainly
/// here because TRADEMARKS.md used to say otherwise. Simple Icons' CC0 waiver covers the
/// traced path data and nothing else: the vendors' trademark rights in the marks those paths
/// depict are untouched by it. TRADEMARKS.md is the full notice, and says what the MIT licence
/// does not grant a fork that keeps these paths.
/// </para>
/// <para>
/// <strong>Every path fills the 16x16 box exactly, on all four sides.</strong> A shape with
/// <c>Stretch="Uniform"</c> is scaled to its own bounds and pinned to the top left of its
/// slot rather than centred, so a mark whose bounds are not square sits visibly off centre
/// beside the label it belongs to - which is what the old placeholder hexagon, 13 wide in a
/// 15 tall box, did. Two of the three are not square on their own 24 unit grid - the burst is
/// 0.08 per cent narrower than it is tall, the knot 1.4 per cent - so each axis is scaled to
/// 15 units on its own; the star is already square there and takes the same factor on both.
/// Every arc in the knot and in the star is circular and unrotated, which is what lets an
/// axis-aligned scale stay exact: each radius takes its own axis and the rotation stays zero.
/// The star's flanks are quadratics, two of them smooth continuations whose control point is
/// the reflection of the one before, so the fitting has to resolve that reflection rather
/// than treat the pair as ordinary coordinates. <c>ProviderGlyphTests</c> holds every mark to
/// all four sides, and holds the star to the share of its box Google's outline covers.
/// </para>
/// <para>
/// <strong>Every path opens <c>F1</c>, the non-zero fill rule.</strong> That is the rule SVG
/// applies when a file names none, so it is the rule the vendors' own files are rendered
/// under and the rule that draws what they draw. On this artwork it changes nothing: no two
/// subpaths overlap, the knot's counters are nested inside its silhouette rather than lapped
/// over one another, and the star's outline never crosses itself, so even-odd produces the
/// same picture - which <c>ProviderGlyphTests</c> proves rather than assumes. It is declared
/// anyway, because re-tracing any of them from a newer vendor asset whose subpaths do lap
/// would otherwise hole those laps out with no error and no failing parse.
/// </para>
/// <para>
/// Neither the accent nor the mark is handed to a view model. Brushes live in the theme
/// dictionaries and are resolved per variant, so a bound brush would be the wrong variant's
/// the moment the system switched; and a <see cref="Geometry"/> cannot be built at all until
/// Avalonia's rendering platform exists. Both are therefore chosen by the style classes keyed
/// off <see cref="IsAnthropic"/>, <see cref="IsOpenAI"/> and <see cref="IsGemini"/>, which a
/// view applies.
/// </para>
/// <para>
/// <strong>Nothing here parses at type initialisation.</strong> <see cref="Geometry.Parse"/>
/// needs <c>IPlatformRenderInterface</c>, so a static field holding a parsed geometry throws
/// inside the type initialiser whenever the type is first touched before the platform is up -
/// and a failed type initialiser is permanent for the life of the process, which turns one
/// early touch into every later use of the type throwing too. The paths are therefore held as
/// strings and parsed on first read, which happens when a style is applied to a control that
/// is already in a visual tree.
/// </para>
/// </remarks>
public static class ProviderIdentity
{
    /// <summary>The provider identifier Claude Code reports.</summary>
    public const string ClaudeId = "claude";

    /// <summary>The provider identifier Codex reports.</summary>
    public const string CodexId = "codex";

    /// <summary>The provider identifier the Gemini CLI reports.</summary>
    public const string GeminiId = "gemini";

    /// <summary>
    /// The Anthropic mark, as path data on the 16px box: Claude's radial burst, whose tapered
    /// spokes run out from a solid centre at uneven lengths, widths and spacings.
    /// </summary>
    /// <remarks>
    /// One closed figure rather than a bundle of them. The burst is a single outline that
    /// travels out along one flank of a spoke and back down the other, all the way round, so
    /// nothing in it overlaps anything else in it. Simple Icons' <c>claude</c> path, traced
    /// from Anthropic's own mark, carried here from its 24 unit grid.
    /// </remarks>
    public const string AnthropicMarkPath =
        "F1 "
        + "M3.443 10.4722 L6.3937 8.8178 L6.4431 8.6736 L6.3937 8.5939 H6.2494 "
        + "L5.7557 8.5635 L4.0696 8.5179 L2.6076 8.4573 L1.1911 8.3814 L0.8342 8.3054 "
        + "L0.5 7.8653 L0.5342 7.6452 L0.8342 7.4441 L1.2633 7.4821 L2.2127 7.5466 "
        + "L3.6367 7.6452 L4.6696 7.7059 L6.2 7.8653 H6.4431 L6.4772 7.7666 "
        + "L6.3937 7.7059 L6.3291 7.6452 L4.8557 6.6472 L3.2608 5.5923 L2.4254 4.9852 "
        + "L1.9735 4.6778 L1.7456 4.3894 L1.6469 3.7596 L2.0571 3.308 L2.6077 3.3459 "
        + "L2.7481 3.3839 L3.3064 3.8126 L4.4988 4.7347 L6.0558 5.8807 L6.2836 6.0704 "
        + "L6.3748 6.0059 L6.3862 5.9604 L6.2836 5.7896 L5.4367 4.2604 L4.5329 2.7046 "
        + "L4.1304 2.0596 L4.0241 1.6725 C3.9861 1.5131 3.9596 1.3804 3.9596 1.2172 "
        + "L4.4267 0.5834 L4.6848 0.5 L5.3076 0.5835 L5.5697 0.8111 L5.957 1.6953 "
        + "L6.5836 3.0879 L7.5557 4.9814 L7.8405 5.543 L7.9924 6.0629 L8.0493 6.2223 "
        + "H8.1481 V6.1312 L8.2279 5.0649 L8.376 3.7558 L8.5203 2.0709 L8.5696 1.5966 "
        + "L8.805 1.0274 L9.2722 0.7201 L9.6367 0.8946 L9.9367 1.3234 L9.8949 1.6004 "
        + "L9.7165 2.7578 L9.3671 4.5716 L9.1392 5.7859 H9.2722 L9.4241 5.6341 "
        + "L10.0392 4.8183 L11.0721 3.5281 L11.5279 3.0158 L12.0595 2.4504 L12.4013 2.181 "
        + "H13.0468 L13.5216 2.8868 L13.3089 3.6154 L12.6443 4.4578 L12.0937 5.1711 "
        + "L11.3038 6.2336 L10.8101 7.0836 L10.8557 7.1519 L10.9734 7.1405 "
        + "L12.7582 6.7611 L13.7228 6.5865 L14.8734 6.3892 L15.3937 6.6321 "
        + "L15.4506 6.8787 L15.2456 7.3834 L14.0152 7.6869 L12.5722 7.9753 "
        + "L10.4228 8.4838 L10.3962 8.5028 L10.4266 8.5408 L11.3949 8.6318 "
        + "L11.8089 8.6546 H12.8228 L14.7102 8.795 L15.2038 9.1213 L15.5 9.5198 "
        + "L15.4506 9.8233 L14.6911 10.2104 L13.6658 9.9675 L11.2734 9.3983 "
        + "L10.4532 9.1934 H10.3392 V9.2617 L11.0228 9.9296 L12.2759 11.0603 "
        + "L13.8443 12.5174 L13.9241 12.8779 L13.7228 13.1626 L13.5101 13.1322 "
        + "L12.1316 12.0963 L11.6 11.6295 L10.3962 10.6164 H10.3164 V10.7226 "
        + "L10.5937 11.1286 L12.0595 13.3295 L12.1354 14.0049 L12.0291 14.225 "
        + "L11.6494 14.3578 L11.2316 14.2819 L10.3734 13.0791 L9.4886 11.7244 "
        + "L8.7747 10.5101 L8.6873 10.5595 L8.2658 15.094 L8.0684 15.3254 L7.6126 15.5 "
        + "L7.2329 15.2116 L7.0316 14.7449 L7.2329 13.8228 L7.476 12.6199 L7.6734 11.6637 "
        + "L7.8519 10.4759 L7.9582 10.0813 L7.9507 10.0547 L7.8633 10.0661 "
        + "L6.9671 11.2956 L5.6038 13.136 L4.5253 14.2895 L4.2671 14.392 L3.819 14.1605 "
        + "L3.8607 13.7469 L4.1114 13.3788 L5.6038 11.4815 L6.5038 10.3053 L7.0849 9.626 "
        + "L7.081 9.5273 H7.0468 L3.0823 12.1001 L2.3759 12.1911 L2.0721 11.9065 "
        + "L2.1102 11.4398 L2.2545 11.288 L3.4469 10.4684 Z";

    /// <summary>
    /// The OpenAI mark, as path data on the 16px box: the interlocking knot, six braided
    /// strands running round an open hexagon at the centre.
    /// </summary>
    /// <remarks>
    /// Eight closed figures: the silhouette the braid makes, six counters that cut its
    /// strands apart, and the hexagon in the middle. Hollow at the centre where the burst is
    /// solid, which is what keeps the two apart at 16px, and what balances an OpenAI accent
    /// that is very nearly the ink colour itself. Simple Icons' <c>openai</c> path, traced
    /// from OpenAI's own mark, carried here from its 24 unit grid.
    /// </remarks>
    public const string OpenAIMarkPath =
        "F1 "
        + "M14.5148 6.6382 A3.792 3.7404 0 0 0 14.188 3.569 "
        + "A3.8309 3.7788 0 0 0 10.0633 1.7565 A3.8429 3.7907 0 0 0 3.5525 3.1137 "
        + "A3.792 3.7404 0 0 0 1.0196 4.9261 A3.8309 3.7788 0 0 0 1.4901 9.3615 "
        + "A3.789 3.7375 0 0 0 1.8139 12.4306 A3.834 3.7818 0 0 0 5.9416 14.2432 "
        + "A3.792 3.7404 0 0 0 8.7983 15.4999 A3.837 3.7848 0 0 0 12.4554 12.8713 "
        + "A3.7949 3.7433 0 0 0 14.9884 11.0588 A3.837 3.7848 0 0 0 14.5148 6.6383 Z "
        + "M8.7983 14.5182 A2.8357 2.7972 0 0 1 6.9758 13.8677 L7.0657 13.8174 "
        + "L10.0933 12.0936 A0.5036 0.4967 0 0 0 10.3421 11.6678 V7.4572 L11.622 8.1876 "
        + "A0.045 0.0444 0 0 1 11.6461 8.2201 V11.7092 A2.8538 2.815 0 0 1 8.7983 14.5182 "
        + "Z M2.6772 11.9398 A2.8327 2.7942 0 0 1 2.3385 10.0563 L2.4285 10.1095 "
        + "L5.459 11.8334 A0.4886 0.482 0 0 0 5.9536 11.8334 L9.6557 9.7281 V11.1858 "
        + "A0.0509 0.0502 0 0 1 9.6346 11.2243 L6.5681 12.9688 "
        + "A2.8507 2.812 0 0 1 2.6772 11.9398 Z M1.8799 5.4348 "
        + "A2.8417 2.8031 0 0 1 3.3787 4.2018 V7.75 A0.4856 0.479 0 0 0 3.6245 8.1728 "
        + "L7.3085 10.2692 L6.0286 10.9995 A0.048 0.0473 0 0 1 5.9836 10.9995 "
        + "L2.923 9.258 A2.8538 2.815 0 0 1 1.8799 5.42 Z M12.3955 7.8446 L8.6994 5.7275 "
        + "L9.9764 5 A0.048 0.0473 0 0 1 10.0214 5 L13.0819 6.7446 "
        + "A2.8477 2.809 0 0 1 12.6533 11.8096 V8.2614 "
        + "A0.5006 0.4937 0 0 0 12.3954 7.8446 Z M13.6695 5.9552 L13.5795 5.9019 "
        + "L10.555 4.1633 A0.4916 0.4849 0 0 0 10.0573 4.1633 L6.3584 6.2686 V4.8109 "
        + "A0.0419 0.0414 0 0 1 6.3764 4.7725 L9.4369 3.0308 "
        + "A2.8507 2.812 0 0 1 13.6695 5.9433 Z M5.6598 8.5394 L4.3799 7.812 "
        + "A0.0509 0.0502 0 0 1 4.3558 7.7766 V4.2964 A2.8507 2.812 0 0 1 9.0292 2.1379 "
        + "L8.9392 2.1882 L5.9117 3.9119 A0.5036 0.4967 0 0 0 5.6628 4.3377 Z "
        + "M6.3553 7.061 L8.0039 6.1236 L9.6557 7.061 V8.9356 L8.0099 9.8729 "
        + "L6.3583 8.9356 Z";

    /// <summary>
    /// The Google Gemini mark, as path data on the 16px box: the four pointed star, whose
    /// arms taper to a point along concave flanks.
    /// </summary>
    /// <remarks>
    /// One closed figure, and the only one of the three drawn in quadratic curves: the
    /// flanks are <c>Q</c> and <c>q</c> segments with two smooth <c>t</c> continuations,
    /// whose control point is the reflection of the one before it, and three circular
    /// unrotated arcs. Nothing in it laps anything else in it. Simple Icons'
    /// <c>googlegemini</c> path, traced from Google's own brand asset, carried here from
    /// its 24 unit grid. It is the only mark of the three that is already square there, so
    /// both axes take the same factor and the outline is not distorted at all.
    /// </remarks>
    public const string GeminiMarkPath =
        "F1 "
        + "M7.4 12.575 Q8 13.9438 8 15.5 q0 -1.5563 0.5813 -2.925 q0.6 "
        + "-1.3687 1.6125 -2.3813 t2.3813 -1.5938 Q13.9438 8 15.5 8 "
        + "q-1.5563 0 -2.925 -0.5813 a7.6875 7.6875 0 0 1 -2.3813 -1.6125 "
        + "a7.6875 7.6875 0 0 1 -1.6125 -2.3813 Q8 2.0563 8 0.5 q0 1.5563 "
        + "-0.6 2.925 q-0.5813 1.3687 -1.5938 2.3813 a7.6875 7.6875 0 0 1 "
        + "-2.3813 1.6125 Q2.0563 8 0.5 8 q1.5563 0 2.925 0.6 q1.3687 "
        + "0.5813 2.3813 1.5938 t1.5938 2.3813";

    /// <summary>The mark any other provider wears, as path data on the 16px box.</summary>
    public const string CirclePath = "M 8,0.5 A 7.5,7.5 0 1 0 8,15.5 A 7.5,7.5 0 1 0 8,0.5 Z";

    private static readonly Lazy<Geometry> LazyAnthropicMark = Lazily(AnthropicMarkPath);
    private static readonly Lazy<Geometry> LazyOpenAIMark = Lazily(OpenAIMarkPath);
    private static readonly Lazy<Geometry> LazyGeminiMark = Lazily(GeminiMarkPath);
    private static readonly Lazy<Geometry> LazyCircle = Lazily(CirclePath);

    /// <inheritdoc cref="AnthropicMarkPath" />
    public static Geometry AnthropicMark => LazyAnthropicMark.Value;

    /// <inheritdoc cref="OpenAIMarkPath" />
    public static Geometry OpenAIMark => LazyOpenAIMark.Value;

    /// <inheritdoc cref="GeminiMarkPath" />
    public static Geometry GeminiMark => LazyGeminiMark.Value;

    /// <summary>The default mark. Parsed on first read, never at type initialisation.</summary>
    public static Geometry Circle => LazyCircle.Value;

    /// <summary>The mark for a provider identifier.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    /// <remarks>
    /// Only a view calls this, and only once a control is on screen. A view model that held
    /// the result would be a view model that cannot be constructed without a render platform.
    /// </remarks>
    public static Geometry Glyph(string? providerId) =>
        IsAnthropic(providerId) ? AnthropicMark
        : IsOpenAI(providerId) ? OpenAIMark
        : IsGemini(providerId) ? GeminiMark
        : Circle;

    /// <summary>The path data for a provider identifier, which needs no platform at all.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static string GlyphPath(string? providerId) =>
        IsAnthropic(providerId) ? AnthropicMarkPath
        : IsOpenAI(providerId) ? OpenAIMarkPath
        : IsGemini(providerId) ? GeminiMarkPath
        : CirclePath;

    /// <summary>Whether this provider wears the Anthropic accent.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static bool IsAnthropic(string? providerId) =>
        string.Equals(providerId, ClaudeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this provider wears the OpenAI accent.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static bool IsOpenAI(string? providerId) =>
        string.Equals(providerId, CodexId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this provider wears the Gemini accent.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static bool IsGemini(string? providerId) =>
        string.Equals(providerId, GeminiId, StringComparison.OrdinalIgnoreCase);

    private static Lazy<Geometry> Lazily(string path) =>
        new(() => Geometry.Parse(path), LazyThreadSafetyMode.ExecutionAndPublication);
}
