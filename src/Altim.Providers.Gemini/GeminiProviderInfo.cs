namespace Altim.Providers.Gemini;

/// <summary>
/// Identity of the Gemini CLI integration, separated from the reader so the composition
/// root can name the provider before anything has been read.
/// </summary>
public static class GeminiProviderInfo
{
    /// <summary>The persisted identifier for this provider.</summary>
    public const string Id = ProviderIds.Gemini;

    /// <summary>The name shown in the UI.</summary>
    public const string DisplayName = "Gemini CLI";
}
