using Avalonia.Media;

namespace Altim.UI.Formatting;

/// <summary>
/// The mark and the accent that carry a provider's identity.
/// </summary>
/// <remarks>
/// <para>
/// Each of the two providers is drawn as its own vendor's mark: Claude's radial burst of
/// tapered spokes, Codex's knotted hexagonal form. Anything else wears the neutral circle.
/// Every mark is a single monochrome path on the 16px box, filled in the provider's accent,
/// because the accent is what carries identity - a mark drawn in its vendor's own colours
/// would repeat what the palette already says, in colours the palette does not hold. The
/// views stretch that box to 16, 20 or 28 depending on what the mark is standing beside.
/// </para>
/// <para>
/// These marks identify the vendors' own products, which is nominative use. Altim claims no
/// endorsement by, or affiliation with, Anthropic or OpenAI. Neither mark is restyled or
/// combined with Altim's own, and neither stands for Altim. DESIGN.md records the same.
/// </para>
/// <para>
/// <strong>Every path fills the 16x16 box exactly, on all four sides.</strong> A shape with
/// <c>Stretch="Uniform"</c> is scaled to its own bounds and pinned to the top left of its
/// slot rather than centred, so a mark whose bounds are not square sits visibly off centre
/// beside the label it belongs to - which is what the old placeholder hexagon, 13 wide in a
/// 15 tall box, did. <c>ProviderGlyphTests</c> holds every mark to all four sides.
/// </para>
/// <para>
/// <strong>Every path opens <c>F1</c>, the non-zero fill rule.</strong> The knot is six bars
/// that lap over one another at the corners, and under the default even-odd rule each lap
/// would cancel itself out into a hole. The burst does not overlap itself today, but it
/// carries the rule too so that widening a spoke cannot quietly punch holes in it.
/// </para>
/// <para>
/// Neither the accent nor the mark is handed to a view model. Brushes live in the theme
/// dictionaries and are resolved per variant, so a bound brush would be the wrong variant's
/// the moment the system switched; and a <see cref="Geometry"/> cannot be built at all until
/// Avalonia's rendering platform exists. Both are therefore chosen by the style classes keyed
/// off <see cref="IsAnthropic"/> and <see cref="IsOpenAI"/>, which a view applies.
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

    /// <summary>
    /// The Anthropic mark, as path data on the 16px box: a radial burst of nine tapered
    /// spokes of uneven length, pinched where they meet and blunt at the tip.
    /// </summary>
    /// <remarks>
    /// Nine is what the box holds. A spoke narrower than about a pixel and a half is grey
    /// smear rather than a spoke at 16px, and nine of them at that weight still leave a gap
    /// between neighbours out at the rim.
    /// </remarks>
    public const string AnthropicMarkPath =
        "F1 "
        + "M 7.73,8.08 L 9.06,5 L 8.82,0.55 L 8.31,0.5 L 7.11,4.81 Z "
        + "M 7.73,8.08 L 10.53,6.8 L 12.96,3.61 L 12.61,3.26 L 9.17,5.46 Z "
        + "M 7.73,8.08 L 11.06,8.79 L 15.5,7.8 L 15.46,7.32 L 10.92,6.93 Z "
        + "M 7.73,8.08 L 9.65,10.55 L 13.52,12.38 L 13.82,11.98 L 10.8,9.05 Z "
        + "M 7.73,8.08 L 7.59,11.39 L 9.42,15.5 L 9.91,15.38 L 9.49,10.94 Z "
        + "M 7.73,8.08 L 5.54,10.23 L 4.38,14.06 L 4.83,14.28 L 7.3,11.04 Z "
        + "M 7.73,8.08 L 4.39,8.47 L 0.63,10.82 L 0.83,11.26 L 5.19,10.17 Z "
        + "M 7.73,8.08 L 4.99,6.36 L 0.64,5.89 L 0.5,6.36 L 4.45,8.15 Z "
        + "M 7.73,8.08 L 6.69,4.95 L 3.54,1.73 L 3.12,2 L 5.07,5.99 Z";

    /// <summary>
    /// The OpenAI mark, as path data on the 16px box: the knotted hexagonal form, drawn as
    /// six bars of one weight, each turned a little off its edge so that consecutive bars
    /// lap past one another at the corners and the ring reads as woven rather than welded.
    /// </summary>
    /// <remarks>
    /// Hollow where the burst is solid, which is what keeps the two apart at 16px, and what
    /// balances an OpenAI accent that is very nearly the ink colour itself.
    /// </remarks>
    public const string OpenAIMarkPath =
        "F1 "
        + "M 6.79,1.83 L 13.91,6.65 L 15.11,5.32 L 7.98,0.5 Z "
        + "M 13.56,4 L 12.3,11.76 L 14.23,12 L 15.48,4.24 Z "
        + "M 14.77,10.17 L 6.39,13.11 L 7.12,14.67 L 15.5,11.74 Z "
        + "M 9.21,14.17 L 2.09,9.35 L 0.89,10.68 L 8.02,15.5 Z "
        + "M 2.44,12 L 3.7,4.24 L 1.77,4 L 0.52,11.76 Z "
        + "M 1.23,5.83 L 9.61,2.89 L 8.88,1.33 L 0.5,4.26 Z";

    /// <summary>The mark any other provider wears, as path data on the 16px box.</summary>
    public const string CirclePath = "M 8,0.5 A 7.5,7.5 0 1 0 8,15.5 A 7.5,7.5 0 1 0 8,0.5 Z";

    private static readonly Lazy<Geometry> LazyAnthropicMark = Lazily(AnthropicMarkPath);
    private static readonly Lazy<Geometry> LazyOpenAIMark = Lazily(OpenAIMarkPath);
    private static readonly Lazy<Geometry> LazyCircle = Lazily(CirclePath);

    /// <inheritdoc cref="AnthropicMarkPath" />
    public static Geometry AnthropicMark => LazyAnthropicMark.Value;

    /// <inheritdoc cref="OpenAIMarkPath" />
    public static Geometry OpenAIMark => LazyOpenAIMark.Value;

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
        : Circle;

    /// <summary>The path data for a provider identifier, which needs no platform at all.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static string GlyphPath(string? providerId) =>
        IsAnthropic(providerId) ? AnthropicMarkPath
        : IsOpenAI(providerId) ? OpenAIMarkPath
        : CirclePath;

    /// <summary>Whether this provider wears the Anthropic accent.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static bool IsAnthropic(string? providerId) =>
        string.Equals(providerId, ClaudeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this provider wears the OpenAI accent.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static bool IsOpenAI(string? providerId) =>
        string.Equals(providerId, CodexId, StringComparison.OrdinalIgnoreCase);

    private static Lazy<Geometry> Lazily(string path) =>
        new(() => Geometry.Parse(path), LazyThreadSafetyMode.ExecutionAndPublication);
}
