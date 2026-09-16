using Avalonia.Media;

namespace Altim.UI.Formatting;

/// <summary>
/// The mark and the accent that carry a provider's identity.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN.md names two provider accents and says the glyph marks identity and nothing else,
/// but it does not supply mark artwork, so the marks here are plain geometric shapes on the
/// 16px box: a diamond for Anthropic, a hexagon for OpenAI, a circle for anything else. The
/// views stretch that box to 16, 20 or 28 depending on what the mark is standing beside.
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

    /// <summary>The Anthropic mark, as path data on the 16px box.</summary>
    public const string DiamondPath = "M 8,0.5 L 15.5,8 L 8,15.5 L 0.5,8 Z";

    /// <summary>The OpenAI mark, as path data on the 16px box.</summary>
    public const string HexagonPath = "M 8,0.5 L 14.5,4.25 L 14.5,11.75 L 8,15.5 L 1.5,11.75 L 1.5,4.25 Z";

    /// <summary>The mark any other provider wears, as path data on the 16px box.</summary>
    public const string CirclePath = "M 8,0.5 A 7.5,7.5 0 1 0 8,15.5 A 7.5,7.5 0 1 0 8,0.5 Z";

    private static readonly Lazy<Geometry> LazyDiamond = Lazily(DiamondPath);
    private static readonly Lazy<Geometry> LazyHexagon = Lazily(HexagonPath);
    private static readonly Lazy<Geometry> LazyCircle = Lazily(CirclePath);

    /// <summary>The Anthropic mark. Parsed on first read, never at type initialisation.</summary>
    public static Geometry Diamond => LazyDiamond.Value;

    /// <summary>The OpenAI mark. Parsed on first read, never at type initialisation.</summary>
    public static Geometry Hexagon => LazyHexagon.Value;

    /// <summary>The default mark. Parsed on first read, never at type initialisation.</summary>
    public static Geometry Circle => LazyCircle.Value;

    /// <summary>The mark for a provider identifier.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    /// <remarks>
    /// Only a view calls this, and only once a control is on screen. A view model that held
    /// the result would be a view model that cannot be constructed without a render platform.
    /// </remarks>
    public static Geometry Glyph(string? providerId) =>
        IsAnthropic(providerId) ? Diamond
        : IsOpenAI(providerId) ? Hexagon
        : Circle;

    /// <summary>The path data for a provider identifier, which needs no platform at all.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static string GlyphPath(string? providerId) =>
        IsAnthropic(providerId) ? DiamondPath
        : IsOpenAI(providerId) ? HexagonPath
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
