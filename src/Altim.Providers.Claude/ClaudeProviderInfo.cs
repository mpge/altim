namespace Altim.Providers.Claude;

/// <summary>
/// Identity of the Claude Code integration, separated from the reader so the
/// composition root can name the provider before anything has been read.
/// </summary>
public static class ClaudeProviderInfo
{
    /// <summary>The persisted identifier for this provider.</summary>
    public const string Id = ProviderIds.Claude;

    /// <summary>The name shown in the UI.</summary>
    public const string DisplayName = "Claude Code";
}
