using Altim.Providers;
using Altim.Providers.Claude;
using Altim.Providers.Codex;
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
    }

    [Fact]
    public void EachProjectUsesTheSharedIdentifier()
    {
        Assert.Equal(ProviderIds.Claude, ClaudeProviderInfo.Id);
        Assert.Equal(ProviderIds.Codex, CodexProviderInfo.Id);
    }

    [Fact]
    public void IdsAreDistinctAndDisplayNamesAreSet()
    {
        Assert.NotEqual(ClaudeProviderInfo.Id, CodexProviderInfo.Id);
        Assert.NotEmpty(ClaudeProviderInfo.DisplayName);
        Assert.NotEmpty(CodexProviderInfo.DisplayName);
    }
}
