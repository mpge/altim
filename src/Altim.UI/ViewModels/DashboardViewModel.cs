using System.Collections.ObjectModel;
using Altim.Core.Abstractions;
using Altim.Core.Settings;
using Altim.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// The dashboard window: a 200px sidebar and the page it selects.
/// </summary>
/// <remarks>
/// <para>
/// The window is built on demand and closed on dismiss, so this view model owns the provider rows
/// it creates and releases them on <see cref="Dispose"/>. Nothing is read during construction: a
/// provider that takes ten seconds to answer costs the window nothing at all, and every page
/// loads when it is first shown.
/// </para>
/// <para>
/// Sections are Overview, one page per provider, History and Settings. Adding a provider adds a
/// section without a view changing.
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly List<ProviderViewModel> _providers = [];
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private NavigationItemViewModel? _selectedSection;

    /// <summary>Initializes the dashboard over every configured provider.</summary>
    /// <param name="providers">The providers to report on.</param>
    /// <param name="history">The store the history page reads.</param>
    /// <param name="settings">The seam the settings page round-trips through.</param>
    /// <param name="timeProvider">The clock every time on screen is measured against.</param>
    public DashboardViewModel(
        IEnumerable<IUsageProvider> providers,
        IUsageHistoryService history,
        ISettingsStore settings,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        foreach (IUsageProvider provider in providers)
        {
            _providers.Add(new ProviderViewModel(provider, timeProvider, AltimSettings.Default));
        }

        Overview = new OverviewViewModel(_providers, timeProvider);
        History = new HistoryViewModel(_providers, history, timeProvider);
        Settings = new SettingsViewModel(settings, history, _providers);
        Settings.HistoryCleared += OnHistoryCleared;

        Sections.Add(new NavigationItemViewModel(Overview));
        foreach (ProviderViewModel provider in _providers)
        {
            Sections.Add(new NavigationItemViewModel(
                new ProviderPageViewModel(provider),
                provider.Glyph,
                provider.IsAnthropic,
                provider.IsOpenAI));
        }

        Sections.Add(new NavigationItemViewModel(History));
        Sections.Add(new NavigationItemViewModel(Settings));

        // Assigned through the field so opening the window does not start a read before
        // the caller has decided to load anything.
        _selectedSection = Sections[0];
    }

    /// <summary>The sidebar's rows, in order.</summary>
    public ObservableCollection<NavigationItemViewModel> Sections { get; } = [];

    /// <summary>The page the selected row shows.</summary>
    public IDashboardPage? CurrentPage => SelectedSection?.Page;

    /// <summary>The Overview page.</summary>
    public OverviewViewModel Overview { get; }

    /// <summary>The History page.</summary>
    public HistoryViewModel History { get; }

    /// <summary>The Settings page.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The provider rows, shared by Overview, the provider pages and Settings.</summary>
    public IReadOnlyList<ProviderViewModel> Providers => _providers;

    /// <summary>The window's title.</summary>
    public string WindowTitle => "Altim";

    /// <summary>Loads the stored settings, then the page currently selected.</summary>
    /// <param name="ct">Cancels the load.</param>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        // Settings first: the thresholds it carries are what the meter ticks are drawn at.
        await Settings.LoadAsync(ct).ConfigureAwait(true);

        if (CurrentPage is { } page)
        {
            await page.LoadAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>Releases every provider row.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Settings.HistoryCleared -= OnHistoryCleared;
        foreach (ProviderViewModel provider in _providers)
        {
            provider.Dispose();
        }
    }

    partial void OnSelectedSectionChanged(NavigationItemViewModel? value)
    {
        if (value is not null)
        {
            _ = value.Page.LoadAsync(CancellationToken.None);
        }
    }

    private void OnHistoryCleared(object? sender, EventArgs e) =>
        _ = History.LoadAsync(CancellationToken.None);
}
