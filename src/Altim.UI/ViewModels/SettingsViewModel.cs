using System.Collections.ObjectModel;
using System.ComponentModel;
using Altim.Core.Abstractions;
using Altim.Core.Settings;
using Altim.UI.Formatting;
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
        nameof(AllowNetworkCalls),
        nameof(ClaudeStatusLineEnabled),
    ];

    private readonly ISettingsStore _store;
    private readonly IUsageHistoryService _history;
    private readonly IStatusLineService _statusLine;
    private readonly List<ProviderViewModel> _providers;
    private readonly Lock _saveLock = new();
    private AltimSettings _current = AltimSettings.Default;
    private Task _saving = Task.CompletedTask;
    private int _saveGeneration;
    private bool _applying;
    private bool _followingStatusLine;
    private bool? _knownStatusLineInstalled;

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
    [NotifyPropertyChangedFor(nameof(ShowsLocalOnlyNotice))]
    private bool _allowNetworkCalls = AltimSettings.Default.AllowNetworkCalls;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLineNotice))]
    [NotifyPropertyChangedFor(nameof(ShowsStatusLineNotice))]
    [NotifyPropertyChangedFor(nameof(CanChangeStatusLine))]
    private StatusLineInstallState _statusLineState = StatusLineInstallState.NotInstalled;

    [ObservableProperty]
    private bool _claudeStatusLineEnabled;

    [ObservableProperty]
    private bool _saveFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    private bool _loadFailed;

    [ObservableProperty]
    private string? _clearHistoryResult;

    /// <summary>Initializes the page.</summary>
    /// <param name="store">The settings seam values are read from and written to.</param>
    /// <param name="history">The store the clear action empties.</param>
    /// <param name="statusLine">The seam the status-line switch installs and reverts through.</param>
    /// <param name="providers">The provider rows the providers section lists.</param>
    public SettingsViewModel(
        ISettingsStore store,
        IUsageHistoryService history,
        IStatusLineService statusLine,
        IEnumerable<ProviderViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(statusLine);
        ArgumentNullException.ThrowIfNull(providers);

        _store = store;
        _history = history;
        _statusLine = statusLine;
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

    /// <summary>The label on the live-quota toggle.</summary>
    public string AllowNetworkCallsLabel => "Check live quota with the provider";

    /// <summary>
    /// What the live-quota toggle actually does, in the terms of the thing it does it to.
    /// </summary>
    /// <remarks>
    /// Written to be true rather than reassuring. Altim has no servers of its own and makes
    /// no vendor API calls itself; what it does is run the provider's own command line, and
    /// that command contacts the vendor with the credentials the user already gave it. The
    /// sentence says exactly that, because "allow network calls" on its own would leave a
    /// privacy-minded reader guessing who is being called and with what.
    /// </remarks>
    public string AllowNetworkCallsDescription =>
        "Asks each provider's own command line for the current figures. That command contacts "
        + "the vendor using the sign-in you already gave it. Altim never contacts a vendor "
        + "itself and sends nothing anywhere.";

    /// <summary>Shown under the toggle while live quota checks are switched off.</summary>
    public string LocalOnlyNotice => UsageFormat.LocalFiguresOnly;

    /// <summary>The label on the status-line switch.</summary>
    public string ClaudeStatusLineLabel => "Add Altim's status line to Claude Code";

    /// <summary>
    /// What the status-line switch does, said plainly, because it edits a file Altim does
    /// not own.
    /// </summary>
    /// <remarks>
    /// Three things have to be in it: what is written and where, what Altim gets back for
    /// it, and that it can be undone. A reader who cannot tell from the copy that this
    /// changes their Claude Code configuration has not been asked properly.
    /// </remarks>
    public string ClaudeStatusLineDescription =>
        "Claude Code runs a short Altim command every time it redraws its status line, and "
        + "shows the result. It is the only place reset times, the spend limit and the "
        + "five-hour and weekly percentages are published, so they stay unavailable without "
        + "it. Altim adds one entry to your Claude Code settings file, copies the file first, "
        + "and removes the entry again when you switch this off.";

    /// <summary>
    /// What is standing in the way, or null when nothing is. Shown under the switch.
    /// </summary>
    public string? StatusLineNotice => StatusLineState switch
    {
        StatusLineInstallState.AnotherStatusLine =>
            "Claude Code already has a status line of its own. Altim will not replace it. "
            + "Remove it from your Claude Code settings first if you want this instead.",
        StatusLineInstallState.NoConfiguration =>
            "Claude Code was not found on this machine, so there is nothing to add a status "
            + "line to.",
        StatusLineInstallState.Failed =>
            "Your Claude Code settings file could not be read or updated. Nothing was changed.",
        _ => null,
    };

    /// <summary>True while there is something to say under the status-line switch.</summary>
    public bool ShowsStatusLineNotice => StatusLineNotice is not null;

    /// <summary>
    /// False when the switch cannot do anything: there is no Claude Code to configure, the
    /// user's own status line is in the way, or the settings file could not be read at all.
    /// The notice says which.
    /// </summary>
    /// <remarks>
    /// The third was missing, and it is the one that could do damage. A file Altim cannot
    /// read is a file Altim does not know the contents of; leaving the switch live over one
    /// meant a user could flip it, Altim would record the flip, and the entry in the file
    /// would go on being whatever it already was.
    /// </remarks>
    public bool CanChangeStatusLine =>
        StatusLineState is not (StatusLineInstallState.AnotherStatusLine
            or StatusLineInstallState.NoConfiguration
            or StatusLineInstallState.Failed);

    /// <summary>
    /// The install or revert currently in flight, or a completed task when there is none.
    /// </summary>
    /// <remarks>
    /// Flipping the switch starts the work rather than awaiting it, the same way every other
    /// change on this page saves itself. This is the handle on it, for a caller that needs to
    /// know the settings file has actually been written.
    /// </remarks>
    public Task StatusLineChange { get; private set; } = Task.CompletedTask;

    /// <summary>True while the figures on screen come only from local files.</summary>
    public bool ShowsLocalOnlyNotice => !AllowNetworkCalls;

    /// <summary>The label on the action that empties the history.</summary>
    public string ClearHistoryLabel => "Clear usage history";

    /// <summary>The product name.</summary>
    public string AppName => "Altim";

    /// <summary>Where the product lives.</summary>
    /// <remarks>
    /// The repository, not altim.dev. That domain is not ours: it resolves to a parking page
    /// offering itself for sale, and sending somebody who clicked "About" to a domain listing
    /// is worse than sending them nowhere. If it is ever bought, this is one line.
    /// </remarks>
    public string Website => "github.com/mpge/altim";

    /// <summary>Shown when a write to the settings store failed.</summary>
    public string SaveFailedText => "Settings could not be saved";

    /// <summary>Shown when the stored settings could not be read.</summary>
    /// <remarks>
    /// A read that failed and a write that failed are different events and used to render as
    /// the same sentence. This one also has to say what the controls underneath it are
    /// showing, because they are not the user's settings: the page is built on
    /// <see cref="AltimSettings.Default"/> and a failed read leaves it there.
    /// </remarks>
    public string LoadFailedText =>
        "Settings could not be read. The values below are the defaults rather than yours, so "
        + "nothing here can be changed until the stored settings can be read.";

    /// <summary>
    /// Whether the page may be edited at all.
    /// </summary>
    /// <remarks>
    /// <b>False while a read has failed</b>, and this is the most serious thing this page
    /// does. The page renders <see cref="AltimSettings.Default"/> from its constructor, and a
    /// failed read leaves every control showing a default as though it were the user's own
    /// choice. <c>_current</c> stays at Default too, so the first change anybody made wrote
    /// Default-plus-that-change over the settings that were actually stored: one unreadable
    /// read and one toggle, and the user's configuration was gone. <see cref="SaveAsync"/>
    /// refuses as well, so a change arriving from anywhere else cannot do it either.
    /// </remarks>
    public bool CanEditSettings => !LoadFailed;

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
            LoadFailed = false;
        }
        catch (OperationCanceledException)
        {
            // The page closed before the read returned.
        }
        catch (Exception)
        {
            // A read that failed is not a write that failed, and saying "Settings could not
            // be saved" here reported an event that had not happened. What did happen is
            // that the page is still showing its constructor's defaults, which are not the
            // user's settings, so it says so and locks itself: the first change anybody made
            // would otherwise have written those defaults over the stored record.
            LoadFailed = true;
        }

        // The stored flag records what the user asked for; Claude Code's own settings file
        // is what is true. Read it, and let the switch follow it.
        await RefreshStatusLineAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Installs or reverts the status line, then shows whatever the settings file holds
    /// afterwards.
    /// </summary>
    /// <param name="install">True to add Altim's entry, false to remove it.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <remarks>
    /// The outcome is read back rather than assumed, so a refusal to replace somebody's own
    /// status line, or a settings file that could not be written, leaves the switch showing
    /// what is actually configured and the notice saying why.
    /// </remarks>
    public async Task ApplyStatusLineAsync(bool install, CancellationToken ct = default)
    {
        try
        {
            StatusLineInstallState state = await BackgroundWork
                .RunAsync(token => _statusLine.SetAsync(install, token), ct)
                .ConfigureAwait(true);
            Follow(state);
        }
        catch (OperationCanceledException)
        {
            // The page closed before the write returned.
        }
        catch (Exception)
        {
            Follow(StatusLineInstallState.Failed);
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
        // The stored record was never read, so _current is Default and every value on the
        // page is Default. Writing now would put Default into the store on top of whatever
        // is actually there, and settings the user cannot see are settings the user cannot
        // put back.
        if (LoadFailed)
        {
            return Task.CompletedTask;
        }

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
            AllowNetworkCalls = AllowNetworkCalls,
            ClaudeStatusLineEnabled = ClaudeStatusLineEnabled,
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

    /// <summary>
    /// The switch moved. Anything that is not the user moving it is already following the
    /// settings file and must not start a second write.
    /// </summary>
    partial void OnClaudeStatusLineEnabledChanged(bool value)
    {
        if (_applying || _followingStatusLine)
        {
            return;
        }

        StatusLineChange = ApplyStatusLineAsync(value, CancellationToken.None);
    }

    private async Task RefreshStatusLineAsync(CancellationToken ct)
    {
        try
        {
            StatusLineInstallState state = await BackgroundWork
                .RunAsync(_statusLine.InspectAsync, ct)
                .ConfigureAwait(true);
            Follow(state);
        }
        catch (OperationCanceledException)
        {
            // The page closed before the read returned.
        }
        catch (Exception)
        {
            Follow(StatusLineInstallState.Failed);
        }
    }

    /// <summary>
    /// Points the switch at what the settings file actually holds.
    /// </summary>
    /// <remarks>
    /// The correction is deliberately not guarded the way <see cref="Apply"/> is: it must
    /// still reach the store, so a request that did not take is not left on record as though
    /// it had. It is guarded against re-entering the install, which is the part that would
    /// loop.
    /// </remarks>
    private void Follow(StatusLineInstallState state)
    {
        StatusLineState = state;

        // Failed says the settings file could not be read or updated and that nothing was
        // changed, so what is true is whatever was last actually observed rather than "off".
        // Mapping it to off made a file that could not be read render as "definitely not
        // installed", and because the switch moved, the handler wrote that guess into the
        // store. A failed inspection at load has nothing observed behind it at all, and then
        // the switch keeps showing what the user asked for while CanChangeStatusLine stops
        // anybody acting on a state nobody knows.
        if (state is StatusLineInstallState.Failed)
        {
            if (_knownStatusLineInstalled is { } known)
            {
                Show(known);
            }

            return;
        }

        bool installed = state is StatusLineInstallState.Installed;
        _knownStatusLineInstalled = installed;
        Show(installed);
    }

    /// <summary>Moves the switch without re-entering the install it is following.</summary>
    /// <param name="installed">What the settings file holds.</param>
    private void Show(bool installed)
    {
        if (ClaudeStatusLineEnabled == installed)
        {
            return;
        }

        _followingStatusLine = true;
        try
        {
            ClaudeStatusLineEnabled = installed;
        }
        finally
        {
            _followingStatusLine = false;
        }
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
            AllowNetworkCalls = settings.AllowNetworkCalls;
            ClaudeStatusLineEnabled = settings.ClaudeStatusLineEnabled;
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
