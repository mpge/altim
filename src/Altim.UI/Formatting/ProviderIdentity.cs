using Avalonia.Media;

namespace Altim.UI.Formatting;

/// <summary>
/// The 16px mark and the accent that carry a provider's identity.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN.md names two provider accents and says the glyph marks identity at 16px and nothing
/// else, but it does not supply mark artwork, so the marks here are plain geometric shapes on
/// the 16px box: a diamond for Anthropic, a hexagon for OpenAI, a circle for anything else.
/// </para>
/// <para>
/// The accent itself is not returned as a brush. Brushes live in the theme dictionaries and are
/// resolved per variant, so views pick them through style classes keyed off
/// <see cref="IsAnthropic"/> and <see cref="IsOpenAI"/> and a theme change follows automatically.
/// </para>
/// </remarks>
public static class ProviderIdentity
{
    /// <summary>The provider identifier Claude Code reports.</summary>
    public const string ClaudeId = "claude";

    /// <summary>The provider identifier Codex reports.</summary>
    public const string CodexId = "codex";

    private static readonly Geometry DiamondGlyph = Geometry.Parse("M 8,0.5 L 15.5,8 L 8,15.5 L 0.5,8 Z");

    private static readonly Geometry HexagonGlyph =
        Geometry.Parse("M 8,0.5 L 14.5,4.25 L 14.5,11.75 L 8,15.5 L 1.5,11.75 L 1.5,4.25 Z");

    private static readonly Geometry CircleGlyph =
        Geometry.Parse("M 8,0.5 A 7.5,7.5 0 1 0 8,15.5 A 7.5,7.5 0 1 0 8,0.5 Z");

    /// <summary>The mark for a provider identifier.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static Geometry Glyph(string? providerId) =>
        IsAnthropic(providerId) ? DiamondGlyph
        : IsOpenAI(providerId) ? HexagonGlyph
        : CircleGlyph;

    /// <summary>Whether this provider wears the Anthropic accent.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static bool IsAnthropic(string? providerId) =>
        string.Equals(providerId, ClaudeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this provider wears the OpenAI accent.</summary>
    /// <param name="providerId">The identifier the provider reports.</param>
    public static bool IsOpenAI(string? providerId) =>
        string.Equals(providerId, CodexId, StringComparison.OrdinalIgnoreCase);
}
