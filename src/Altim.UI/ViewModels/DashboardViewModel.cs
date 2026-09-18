using System.Collections.ObjectModel;
using Altim.Core.Abstractions;
using Altim.Core.Settings;
using Altim.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// The dashboard window: a 192px sidebar and the page it selects.
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
    /// <param name="statusLine">The seam the settings page installs the status line through.</param>
    /// <param name="timeProvider">The clock every time on screen is measured against.</param>
    public DashboardViewModel(
        IEnumerable<IUsageProvider> providers,
        IUsageHistoryService history,
        ISettingsStore settings,
        IStatusLineService statusLine,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(statusLine);
        ArgumentNullException.ThrowIfNull(timeProvider);

        foreach (IUsageProvider provider in providers)
        {
            _providers.Add(new ProviderViewModel(provider, timeProvider, AltimSettings.Default));
        }

        Overview = new OverviewViewModel(_providers, history, timeProvider);
        History = new HistoryViewModel(_providers, history, timeProvider);
        Settings = new SettingsViewModel(settings, history, statusLine, _providers);
        Settings.HistoryCleared += OnHistoryCleared;

        Sections.Add(new NavigationItemViewModel(Overview, NavigationIcon.Overview));
        foreach (ProviderViewModel provider in _providers)
        {
            provider.OpenRequested += OnProviderOpenRequested;
            Sections.Add(new NavigationItemViewModel(
                new ProviderPageViewModel(provider),
                NavigationIcon.Provider,
                provider.IsAnthropic,
                provider.IsOpenAI,
                provider.IsGemini));
        }

        Sections.Add(new NavigationItemViewModel(History, NavigationIcon.History));
        Sections.Add(new NavigationItemViewModel(Settings, NavigationIcon.Settings));

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

    /// <summary>The window's title, and the wordmark in the sidebar's header.</summary>
    public string WindowTitle => "Altim";

    /// <summary>The line under the wordmark in the sidebar's footer.</summary>
    public string Tagline => "AI usage, at a glance.";

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

    /// <summary>
    /// Re-measures every countdown the window shows against the clock.
    /// </summary>
    /// <remarks>
    /// The same problem the tray panel has, and worse here: a dashboard is a window somebody
    /// leaves open. Overview's cards, the provider pages and the rows on them all read their
    /// reset times off these rows, so ticking the rows is the whole of it.
    /// </remarks>
    public void RefreshCountdowns()
    {
        foreach (ProviderViewModel provider in _providers)
        {
            _ = provider.RefreshCountdowns();
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
        Overview.Dispose();
        foreach (ProviderViewModel provider in _providers)
        {
            provider.OpenRequested -= OnProviderOpenRequested;
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

    /// <summary>
    /// A provider card's own disclosure asks for that provider's page. The card knows which
    /// provider it is and nothing about navigation, so the window matches the row to the
    /// section that shows it.
    /// </summary>
    private void OnProviderOpenRequested(object? sender, EventArgs e)
    {
        if (sender is not ProviderViewModel row)
        {
            return;
        }

        foreach (NavigationItemViewModel section in Sections)
        {
            if (section.Page is ProviderPageViewModel page && ReferenceEquals(page.Provider, row))
            {
                SelectedSection = section;
                return;
            }
        }
    }
}
