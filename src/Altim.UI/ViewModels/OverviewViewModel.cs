using System.Collections.ObjectModel;
using Altim.UI.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// The dashboard's first page: a greeting and one card per provider.
/// </summary>
/// <remarks>
/// The greeting is computed from the machine's local time every time the page loads, so a window
/// left open across noon reads correctly the next time it is shown.
/// </remarks>
public sealed partial class OverviewViewModel : ObservableObject, IDashboardPage
{
    private readonly TimeProvider _timeProvider;

    [ObservableProperty]
    private string _greetingText;

    [ObservableProperty]
    private bool _hasProviders;

    /// <summary>Initializes the page over the dashboard's provider rows.</summary>
    /// <param name="providers">The rows, shared with the provider pages.</param>
    /// <param name="timeProvider">The clock the greeting is read from.</param>
    public OverviewViewModel(IEnumerable<ProviderViewModel> providers, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _greetingText = Greeting.Now(timeProvider);

        foreach (ProviderViewModel provider in providers)
        {
            Providers.Add(provider);
        }

        HasProviders = Providers.Count > 0;
    }

    /// <inheritdoc />
    public string Title => "Overview";

    /// <summary>One card per provider.</summary>
    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    /// <summary>The line under the greeting.</summary>
    public string SubHeading => Greeting.SubHeading;

    /// <summary>Shown when no provider is configured at all.</summary>
    public string NoProvidersText => UsageFormat.NoProviders;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken ct)
    {
        GreetingText = Greeting.Now(_timeProvider);

        List<Task> readings = [];
        foreach (ProviderViewModel provider in Providers)
        {
            readings.Add(provider.LoadAsync(ct));
        }

        await Task.WhenAll(readings).ConfigureAwait(true);
    }
}
