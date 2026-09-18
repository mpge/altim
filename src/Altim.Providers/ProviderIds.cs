namespace Altim.Providers;

/// <summary>
/// The provider identifiers shared by every provider project.
/// </summary>
/// <remarks>
/// These strings are persisted: they are the <c>provider_id</c> column of
/// <c>usage_sample</c> and part of the primary key of <c>notification_state</c>.
/// Changing one orphans that provider's history, so they are fixed for the life of
/// the schema.
/// </remarks>
public static class ProviderIds
{
    /// <summary>Claude Code.</summary>
    public const string Claude = "claude";

    /// <summary>OpenAI Codex.</summary>
    public const string Codex = "codex";

    /// <summary>Google Gemini CLI.</summary>
    public const string Gemini = "gemini";
}
