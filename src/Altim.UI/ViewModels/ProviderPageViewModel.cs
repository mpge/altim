using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// One provider page, parameterised by the provider it shows.
/// </summary>
/// <remarks>
/// There is one provider view in the application and one of these per provider. Everything on the
/// page comes from <see cref="Provider"/>, which is the same instance Overview shows, so a
/// reading taken on either surface is on both.
/// </remarks>
public sealed class ProviderPageViewModel : ObservableObject, IDashboardPage
{
    /// <summary>Initializes the page over one provider row.</summary>
    /// <param name="provider">The provider to show in full.</param>
    public ProviderPageViewModel(ProviderViewModel provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Provider = provider;
    }

    /// <inheritdoc />
    public string Title => Provider.DisplayName;

    /// <summary>The provider this page shows.</summary>
    public ProviderViewModel Provider { get; }

    /// <summary>The heading over the metrics section.</summary>
    public string UsageHeading => "Usage";

    /// <summary>The heading over the session list.</summary>
    public string ActivityHeading => "Activity";

    /// <summary>The heading over the integration section.</summary>
    public string IntegrationHeading => "Integration";

    /// <summary>The heading over the token breakdown.</summary>
    public string TokensHeading => "Tokens";

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken ct)
    {
        await Provider.LoadAsync(ct).ConfigureAwait(true);
        await Provider.LoadSessionsAsync(ct).ConfigureAwait(true);
    }
}
