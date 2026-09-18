using Altim.Providers;
using Altim.Providers.Claude;
using Altim.Providers.Codex;
using Altim.Providers.Gemini;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// Provider identifiers are persisted as the <c>provider_id</c> column, so they are
/// pinned here: a rename would orphan that provider's history.
/// </summary>
public sealed class ProviderIdentityTests
{
    [Fact]
    public void IdsAreTheValuesTheSchemaStores()
    {
        Assert.Equal("claude", ClaudeProviderInfo.Id);
        Assert.Equal("codex", CodexProviderInfo.Id);
        Assert.Equal("gemini", GeminiProviderInfo.Id);
    }

    [Fact]
    public void EachProjectUsesTheSharedIdentifier()
    {
        Assert.Equal(ProviderIds.Claude, ClaudeProviderInfo.Id);
        Assert.Equal(ProviderIds.Codex, CodexProviderInfo.Id);
        Assert.Equal(ProviderIds.Gemini, GeminiProviderInfo.Id);
    }

    [Fact]
    public void IdsAreDistinctAndDisplayNamesAreSet()
    {
        Assert.Equal(3, new HashSet<string>(
            [ClaudeProviderInfo.Id, CodexProviderInfo.Id, GeminiProviderInfo.Id],
            StringComparer.Ordinal).Count);

        Assert.NotEmpty(ClaudeProviderInfo.DisplayName);
        Assert.NotEmpty(CodexProviderInfo.DisplayName);
        Assert.NotEmpty(GeminiProviderInfo.DisplayName);
    }
}
