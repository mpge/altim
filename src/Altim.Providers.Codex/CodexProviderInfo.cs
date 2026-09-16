namespace Altim.Providers.Codex;

/// <summary>
/// Identity of the OpenAI Codex integration, separated from the reader so the
/// composition root can name the provider before anything has been read.
/// </summary>
public static class CodexProviderInfo
{
    /// <summary>The persisted identifier for this provider.</summary>
    public const string Id = ProviderIds.Codex;

    /// <summary>The name shown in the UI.</summary>
    public const string DisplayName = "Codex";
}
