using System.Collections.ObjectModel;
using System.ComponentModel;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.Core.Usage;
using Altim.UI.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Altim.UI.ViewModels;

/// <summary>
/// The tray panel: provider rows, reset times, one status line and one action.
/// </summary>
/// <remarks>
/// <para>
/// The panel is built once at start-up and hidden rather than closed, so this view model lives
/// for the life of the process and keeps its provider rows subscribed the whole time. A reading
/// that arrives while the panel is hidden updates it in place, which is what lets the panel open
/// inside the 100ms budget with current numbers already on it.
/// </para>
/// <para>
/// The status line is <see cref="UsageAggregator"/>'s sentence rather than a second opinion
/// written here, resolved through the aggregator's own display name lookup so it reads
/// "Claude Code unavailable" rather than "claude unavailable".
/// </para>
/// </remarks>
public sealed partial class PopupViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    [ObservableProperty]
    private string _statusLine = UsageOverview.Empty.StatusLine;

    [ObservableProperty]
    private bool _hasResets;

    [ObservableProperty]
    private bool _hasProviders;

    /// <summary>Initializes the panel over every configured provider.</summary>
    /// <param name="providers">The providers to report on.</param>
    /// <param name="timeProvider">The clock reset times are measured against.</param>
    /// <param name="settings">Supplies the thresholds the meters tick at.</param>
    public PopupViewModel(
        IEnumerable<IUsageProvider> providers,
        TimeProvider timeProvider,
        AltimSettings settings)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(settings);

        foreach (IUsageProvider provider in providers)
        {
            var row = new ProviderViewModel(provider, timeProvider, settings);
            row.PropertyChanged += OnProviderPropertyChanged;
            Providers.Add(row);
        }

        HasProviders = Providers.Count > 0;
        Rebuild();
    }

    /// <summary>Raised when the panel's action asks for the dashboard.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>One row per provider, in the order they were registered.</summary>
    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    /// <summary>Every reset instant any provider reports, soonest first.</summary>
    public ObservableCollection<ResetRowViewModel> Resets { get; } = [];

    /// <summary>The label on the panel's one action.</summary>
    public string OpenLabel => "Open Altim";

    /// <summary>Shown in place of the resets section when no window reports an instant.</summary>
    public string NoResetsText => UsageFormat.NoResetsReported;

    /// <summary>Shown when no provider is configured at all.</summary>
    public string NoProvidersText => UsageFormat.NoProviders;

    /// <summary>Takes a reading from every provider at once.</summary>
    /// <param name="ct">Cancels the readings.</param>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        List<Task> readings = [];
        foreach (ProviderViewModel provider in Providers)
        {
            readings.Add(provider.LoadAsync(ct));
        }

        await Task.WhenAll(readings).ConfigureAwait(true);
        Rebuild();
    }

    /// <summary>Applies new thresholds to every row.</summary>
    /// <param name="settings">The settings the meters tick against.</param>
    public void ApplySettings(AltimSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (ProviderViewModel provider in Providers)
        {
            provider.ApplySettings(settings);
        }

        Rebuild();
    }

    /// <summary>Releases every provider row.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ProviderViewModel provider in Providers)
        {
            provider.PropertyChanged -= OnProviderPropertyChanged;
            provider.Dispose();
        }
    }

    [RelayCommand]
    private void OpenAltim() => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProviderViewModel.CurrentUsage))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        RebuildStatusLine();
        RebuildResets();
    }

    private void RebuildStatusLine()
    {
        List<ProviderUsage> readings = [];
        foreach (ProviderViewModel provider in Providers)
        {
            readings.Add(provider.CurrentUsage);
        }

        StatusLine = UsageAggregator.DescribeStatus(readings, DisplayNameFor);
    }

    private string DisplayNameFor(string providerId)
    {
        foreach (ProviderViewModel provider in Providers)
        {
            if (string.Equals(provider.Id, providerId, StringComparison.Ordinal))
            {
                return provider.DisplayName;
            }
        }

        return providerId;
    }

    private void RebuildResets()
    {
        List<(DateTimeOffset At, ResetRowViewModel Row)> rows = [];
        foreach (ProviderViewModel provider in Providers)
        {
            foreach (MetricViewModel metric in provider.Metrics)
            {
                if (metric.ResetsAt is not { } instant || metric.RemainingText is not { } remaining)
                {
                    continue;
                }

                rows.Add((instant, new ResetRowViewModel(
                    metric.Label,
                    provider.DisplayName,
                    remaining,
                    metric.ResetClockText)));
            }
        }

        rows.Sort(static (left, right) => left.At.CompareTo(right.At));

        Resets.Clear();
        foreach ((DateTimeOffset _, ResetRowViewModel row) in rows)
        {
            Resets.Add(row);
        }

        HasResets = Resets.Count > 0;
    }
}
