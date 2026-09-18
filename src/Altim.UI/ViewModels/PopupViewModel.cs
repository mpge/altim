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
/// The tray panel: a header, the one reading it leads with on a dial, a line per provider,
/// the next reset each of them has, one status line and one action.
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeadline))]
    private HeadlineReadingViewModel? _headline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIsOk))]
    private bool _statusIsError;

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
            row.OpenRequested += OnProviderOpenRequested;
            Providers.Add(row);
        }

        HasProviders = Providers.Count > 0;
        Rebuild();
    }

    /// <summary>
    /// Raised when the panel asks for the dashboard. The payload is the sidebar section to
    /// open it on, or null for wherever it opens by default: the gear asks for Settings and a
    /// provider's own row asks for that provider, and the panel does not open windows itself.
    /// </summary>
    public event EventHandler<string?>? OpenRequested;

    /// <summary>One row per provider, in the order they were registered.</summary>
    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    /// <summary>Every reset instant any provider reports, soonest first.</summary>
    public ObservableCollection<ResetRowViewModel> Resets { get; } = [];

    /// <summary>Whether there is a window for the dial to show.</summary>
    public bool HasHeadline => Headline is not null;

    /// <summary>The wordmark in the panel's header.</summary>
    public string Title => "Altim";

    /// <summary>The label on the panel's one action.</summary>
    public string OpenLabel => "Open Altim";

    /// <summary>
    /// The accessible name of the gear in the header. The control's whole content is a drawn
    /// icon, so without this a screen reader announces the shape type instead.
    /// </summary>
    public string SettingsLabel => UsageFormat.SettingsActionName;

    /// <summary>The heading over the resets section.</summary>
    public string ResetsLabel => UsageFormat.ResetsLabel;

    /// <summary>Whether the status icon reads as operational.</summary>
    public bool StatusIsOk => !StatusIsError;

    /// <summary>The name of the sidebar section the gear opens.</summary>
    public const string SettingsSection = "Settings";

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
            provider.OpenRequested -= OnProviderOpenRequested;
            provider.Dispose();
        }
    }

    [RelayCommand]
    private void OpenAltim() => OpenRequested?.Invoke(this, null);

    [RelayCommand]
    private void OpenSettings() => OpenRequested?.Invoke(this, SettingsSection);

    private void OnProviderOpenRequested(object? sender, EventArgs e)
    {
        if (sender is ProviderViewModel row)
        {
            OpenRequested?.Invoke(this, row.DisplayName);
        }
    }

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
        RebuildHeadline();
        RebuildResets();
    }

    private void RebuildStatusLine()
    {
        List<ProviderUsage> readings = [];
        foreach (ProviderViewModel provider in Providers)
        {
            readings.Add(provider.CurrentUsage);
        }

        UsageOverview overview = UsageAggregator.Aggregate(readings, DisplayNameFor);
        StatusLine = overview.StatusLine;
        StatusIsError = overview.Status == ProviderStatus.Error;
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

    /// <summary>
    /// Picks the one window the dial shows: <b>the highest level any provider reports</b> -
    /// the window nearest its ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The panel already prints every window's figure on its provider's own line, so the dial
    /// is not there to add a number. It is there to say which of those numbers decides
    /// whether you can keep working, and to be the one place the panel shows the configured
    /// threshold at all.
    /// </para>
    /// <para>
    /// Ranking by the raw level rather than by how near each window is to its own threshold
    /// is deliberate. Every window is drawn against one shared scale, which is the whole
    /// reason two readings can be compared by eye; ranking them by a ratio to a per-window
    /// alert level would order them by something nobody can see on that scale. A tie goes to
    /// the window that rolls over first, because that is the one reached first, and then to
    /// the order the providers were registered in, so the same readings always pick the same
    /// window.
    /// </para>
    /// <para>
    /// A window with no percentage is never picked over one that has a figure, but when no
    /// window anywhere reports one the panel still leads with the first window it has: the
    /// dial draws its unavailable face and the figure is an em dash, which says "nothing was
    /// reported for this" rather than leaving the panel headed by nothing. A panel with no
    /// windows at all has no headline and the section is not drawn - there would be no window
    /// to name, and an unnamed dial is furniture.
    /// </para>
    /// </remarks>
    private void RebuildHeadline()
    {
        MetricViewModel? best = null;
        string? provider = null;

        foreach (ProviderViewModel row in Providers)
        {
            foreach (MetricViewModel metric in row.Metrics)
            {
                if (best is null || Beats(metric, best))
                {
                    best = metric;
                    provider = row.DisplayName;
                }
            }
        }

        Headline = best is not null && provider is not null
            ? new HeadlineReadingViewModel(best, provider)
            : null;
    }

    /// <summary>Whether one metric should be on the dial ahead of another.</summary>
    /// <param name="candidate">The metric being considered.</param>
    /// <param name="holder">The metric currently holding the dial.</param>
    /// <returns>True when the candidate takes it.</returns>
    private static bool Beats(MetricViewModel candidate, MetricViewModel holder)
    {
        if (candidate.Value is not { } level)
        {
            return false;
        }

        if (holder.Value is not { } held)
        {
            return true;
        }

        if (level > held)
        {
            return true;
        }

        if (level < held)
        {
            return false;
        }

        // Level for level, the window that rolls over first is the one reached first. A
        // window reporting no instant never displaces one that does.
        return candidate.ResetsAt is { } instant
            && (holder.ResetsAt is not { } best || instant < best);
    }

    /// <summary>
    /// One line per provider, carrying the soonest window that provider actually reports a
    /// reset instant for. The panel used to list every window of every provider, which on a
    /// machine with two providers and five windows was five lines of small print where the
    /// question being asked is "how long have I got".
    /// </summary>
    private void RebuildResets()
    {
        List<(DateTimeOffset At, ResetRowViewModel Row)> rows = [];
        foreach (ProviderViewModel provider in Providers)
        {
            MetricViewModel? soonest = null;
            foreach (MetricViewModel metric in provider.Metrics)
            {
                if (metric.ResetsAt is not { } instant || metric.RemainingText is null)
                {
                    continue;
                }

                if (soonest?.ResetsAt is not { } best || instant < best)
                {
                    soonest = metric;
                }
            }

            if (soonest is { ResetsAt: { } at, RemainingText: { } remaining })
            {
                rows.Add((at, new ResetRowViewModel(provider.DisplayName, remaining)));
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
