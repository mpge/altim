using System.Collections.ObjectModel;
using System.ComponentModel;
using Altim.Core.Abstractions;
using Altim.Core.Settings;
using Altim.UI.Services;
using Altim.UI.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Altim.UI.ViewModels;

/// <summary>
/// The settings page: general, notifications, providers, privacy and about.
/// </summary>
/// <remarks>
/// <para>
/// Every value round-trips through <see cref="ISettingsStore"/>, which is the only settings seam
/// the interface knows about. A change saves itself: there is no apply button, so a window closed
/// straight after a toggle keeps the toggle.
/// </para>
/// <para>
/// A threshold change also reaches the provider rows immediately, so the meter tick moves at the
/// same time as the number that set it.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject, IDashboardPage
{
    private static readonly HashSet<string> SavedProperties =
    [
        nameof(LaunchAtLogin),
        nameof(StartMinimised),
        nameof(SelectedTheme),
        nameof(SelectedRefresh),
        nameof(NotificationsEnabled),
        nameof(SelectedSessionThreshold),
        nameof(SelectedWeeklyThreshold),
        nameof(ResetAlertsEnabled),
    ];

    private readonly ISettingsStore _store;
    private readonly IUsageHistoryService _history;
    private readonly List<ProviderViewModel> _providers;
    private readonly Lock _saveLock = new();
    private AltimSettings _current = AltimSettings.Default;
    private Task _saving = Task.CompletedTask;
    private int _saveGeneration;
    private bool _applying;

    [ObservableProperty]
    private bool _launchAtLogin;

    [ObservableProperty]
    private bool _startMinimised;

    [ObservableProperty]
    private ThemeOption _selectedTheme = ThemeOption.For(AltimSettings.Default.Theme);

    [ObservableProperty]
    private RefreshOption _selectedRefresh = RefreshOption.Standard[1];

    [ObservableProperty]
    private bool _notificationsEnabled;

    [ObservableProperty]
    private ThresholdOption _selectedSessionThreshold =
        ThresholdOption.For(AltimSettings.DefaultSessionThresholdPercent);

    [ObservableProperty]
    private ThresholdOption _selectedWeeklyThreshold =
        ThresholdOption.For(AltimSettings.DefaultWeeklyThresholdPercent);

    [ObservableProperty]
    private bool _resetAlertsEnabled;

    [ObservableProperty]
    private bool _saveFailed;

    [ObservableProperty]
    private string? _clearHistoryResult;

    /// <summary>Initializes the page.</summary>
    /// <param name="store">The settings seam values are read from and written to.</param>
    /// <param name="history">The store the clear action empties.</param>
    /// <param name="providers">The provider rows the providers section lists.</param>
    public SettingsViewModel(
        ISettingsStore store,
        IUsageHistoryService history,
        IEnumerable<ProviderViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(providers);

        _store = store;
        _history = history;
        _providers = [.. providers];

        foreach (RefreshOption option in RefreshOption.Standard)
        {
            RefreshOptions.Add(option);
        }

        foreach (int threshold in ThresholdOption.Standard)
        {
            ThresholdOptions.Add(ThresholdOption.For(threshold));
        }

        foreach (ProviderViewModel provider in _providers)
        {
            Providers.Add(provider);
        }

        Apply(AltimSettings.Default);
    }

    /// <summary>Raised once the stored history has actually been emptied.</summary>
    public event EventHandler? HistoryCleared;

    /// <inheritdoc />
    public string Title => "Settings";

    /// <summary>The theme choices.</summary>
    public IReadOnlyList<ThemeOption> Themes => ThemeOption.All;

    /// <summary>The refresh interval choices, including a stored value that is not standard.</summary>
    public ObservableCollection<RefreshOption> RefreshOptions { get; } = [];

    /// <summary>The threshold choices, including a stored value that is not standard.</summary>
    public ObservableCollection<ThresholdOption> ThresholdOptions { get; } = [];

    /// <summary>The providers listed in the providers section.</summary>
    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    /// <summary>The headings, in the order the page shows them.</summary>
    public string GeneralHeading => "General";

    /// <summary>The notifications section heading.</summary>
    public string NotificationsHeading => "Notifications";

    /// <summary>The providers section heading.</summary>
    public string ProvidersHeading => "Providers";

    /// <summary>The privacy section heading.</summary>
    public string PrivacyHeading => "Privacy";

    /// <summary>The about section heading.</summary>
    public string AboutHeading => "About";

    /// <summary>What Altim keeps on this machine, in the order PRIVACY.md states it.</summary>
    public IReadOnlyList<string> PrivacyLines => StoredLocally;

    private static readonly string[] StoredLocally =
    [
        "Usage snapshots: percentages, token counts, limit windows and reset times.",
        "Your settings, in the same local database.",
        "No prompts, no commands, no file paths and no project names.",
        "Nothing is sent anywhere. Altim has no servers and no telemetry.",
    ];

    /// <summary>The label on the action that empties the history.</summary>
    public string ClearHistoryLabel => "Clear usage history";

    /// <summary>The product name.</summary>
    public string AppName => "Altim";

    /// <summary>The product site.</summary>
    public string Website => "altim.dev";

    /// <summary>Shown when a write to the settings store failed.</summary>
    public string SaveFailedText => "Settings could not be saved";

    /// <summary>The running version.</summary>
    public string VersionText => Version;

    private static readonly string Version = string.Concat(
        "Version ",
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            AltimSettings settings = await BackgroundWork.RunAsync(_store.GetAsync, ct)
                .ConfigureAwait(true);
            Apply(settings);
        }
        catch (OperationCanceledException)
        {
            // The page closed before the read returned.
        }
        catch (Exception)
        {
            // A failed read leaves the last known values on screen rather than replacing
            // them with defaults that a later change would write back over the real ones.
            SaveFailed = true;
        }
    }

    /// <summary>Writes the current values through the settings store.</summary>
    /// <param name="ct">Cancels the write.</param>
    /// <remarks>
    /// Writes are queued rather than raced. Every change saves itself, so flipping three toggles
    /// in a second asks for three writes; handing them all to the thread pool independently lets
    /// them complete in any order, and the record left in the store would be whichever finished
    /// last rather than the one the user ended on.
    /// </remarks>
    public Task SaveAsync(CancellationToken ct = default)
    {
        AltimSettings next = _current with
        {
            LaunchAtLogin = LaunchAtLogin,
            StartMinimised = StartMinimised,
            Theme = SelectedTheme.Value,
            RefreshInterval = SelectedRefresh.Value,
            NotificationsEnabled = NotificationsEnabled,
            SessionThresholdPercent = SelectedSessionThreshold.Value,
            WeeklyThresholdPercent = SelectedWeeklyThreshold.Value,
            NotifyOnWindowReset = ResetAlertsEnabled,
        };

        _current = next;

        // AltimSettings clamps a threshold on the way in, so the record can come back carrying a
        // different number from the one the picker offered. The picker follows the record rather
        // than the other way round: a picker reading 150% beside a meter ticking at 100% would be
        // showing a threshold that is not the one in force.
        ReselectThresholds(next);
        PushToProviders(next);

        int generation = Interlocked.Increment(ref _saveGeneration);

        lock (_saveLock)
        {
            _saving = WriteAfterAsync(_saving, next, generation, ct);
            return _saving;
        }
    }

    private async Task WriteAfterAsync(
        Task previous,
        AltimSettings settings,
        int generation,
        CancellationToken ct)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The write before this one reported its own outcome through SaveFailed and to its
            // own caller. Failing to write then says nothing about whether this one can write now.
        }

        // A newer save was asked for while this one waited, and it carries everything this one
        // carried. Writing now would put the older record in the store last.
        if (generation != Volatile.Read(ref _saveGeneration))
        {
            return;
        }

        try
        {
            await BackgroundWork.RunAsync(token => _store.SaveAsync(settings, token), ct)
                .ConfigureAwait(true);
            SaveFailed = false;
        }
        catch (OperationCanceledException)
        {
            // The page closed before the write returned.
        }
        catch (Exception)
        {
            SaveFailed = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_applying || e.PropertyName is not { } name || !SavedProperties.Contains(name))
        {
            return;
        }

        _ = SaveAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task ClearHistoryAsync(CancellationToken ct)
    {
        try
        {
            await BackgroundWork.RunAsync(_history.ClearAsync, ct).ConfigureAwait(true);
            ClearHistoryResult = "Usage history cleared.";
            HistoryCleared?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // The page closed before the delete returned.
        }
        catch (Exception)
        {
            ClearHistoryResult = "Usage history could not be cleared.";
        }
    }

    private void Apply(AltimSettings settings)
    {
        _applying = true;
        try
        {
            _current = settings;

            EnsureRefreshOption(settings.RefreshInterval);
            EnsureThreshold(settings.SessionThresholdPercent);
            EnsureThreshold(settings.WeeklyThresholdPercent);

            LaunchAtLogin = settings.LaunchAtLogin;
            StartMinimised = settings.StartMinimised;
            SelectedTheme = ThemeOption.For(settings.Theme);
            SelectedRefresh = FindRefreshOption(settings.RefreshInterval);
            NotificationsEnabled = settings.NotificationsEnabled;
            SelectedSessionThreshold = FindThreshold(settings.SessionThresholdPercent);
            SelectedWeeklyThreshold = FindThreshold(settings.WeeklyThresholdPercent);
            ResetAlertsEnabled = settings.NotifyOnWindowReset;
        }
        finally
        {
            _applying = false;
        }

        PushToProviders(settings);
    }

    private void ReselectThresholds(AltimSettings settings)
    {
        if (SelectedSessionThreshold.Value == settings.SessionThresholdPercent
            && SelectedWeeklyThreshold.Value == settings.WeeklyThresholdPercent)
        {
            return;
        }

        // Guarded, because these are saved properties: reselecting without it would write the
        // value that has just been written, once per clamp, for as long as the page is open.
        _applying = true;
        try
        {
            EnsureThreshold(settings.SessionThresholdPercent);
            EnsureThreshold(settings.WeeklyThresholdPercent);
            SelectedSessionThreshold = FindThreshold(settings.SessionThresholdPercent);
            SelectedWeeklyThreshold = FindThreshold(settings.WeeklyThresholdPercent);
        }
        finally
        {
            _applying = false;
        }
    }

    private void PushToProviders(AltimSettings settings)
    {
        foreach (ProviderViewModel provider in _providers)
        {
            provider.ApplySettings(settings);
        }
    }

    private void EnsureRefreshOption(TimeSpan interval)
    {
        foreach (RefreshOption option in RefreshOptions)
        {
            if (option.Value == interval)
            {
                return;
            }
        }

        RefreshOptions.Add(RefreshOption.Custom(interval));
    }

    private RefreshOption FindRefreshOption(TimeSpan interval)
    {
        foreach (RefreshOption option in RefreshOptions)
        {
            if (option.Value == interval)
            {
                return option;
            }
        }

        return RefreshOptions[0];
    }

    private void EnsureThreshold(int percent)
    {
        for (int i = 0; i < ThresholdOptions.Count; i++)
        {
            if (ThresholdOptions[i].Value == percent)
            {
                return;
            }

            if (ThresholdOptions[i].Value > percent)
            {
                ThresholdOptions.Insert(i, ThresholdOption.For(percent));
                return;
            }
        }

        ThresholdOptions.Add(ThresholdOption.For(percent));
    }

    private ThresholdOption FindThreshold(int percent)
    {
        foreach (ThresholdOption option in ThresholdOptions)
        {
            if (option.Value == percent)
            {
                return option;
            }
        }

        return ThresholdOption.For(percent);
    }
}
