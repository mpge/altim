using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Usage;
using Altim.UI.Formatting;
using Altim.UI.History;
using Altim.UI.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// The dashboard's first page: a heading with the aggregate status, a card per provider, and
/// the last week of history beside what the agents are doing now.
/// </summary>
/// <remarks>
/// <para>
/// The greeting is computed from the machine's local time every time the page loads, so a
/// window left open across noon reads correctly the next time it is shown. It is the eyebrow
/// above the title rather than the title itself: the title names the page, which is what a
/// person navigating back to it is looking for.
/// </para>
/// <para>
/// The history panel deliberately duplicates the History page at one span. Overview answers
/// "where am I now"; the shape of the last week is part of that answer, and the History page
/// is where the other two spans live.
/// </para>
/// <para>
/// Pacing is computed here rather than in the provider row, because it is the one figure on
/// the card that comes from local history rather than from the provider. The row is told the
/// answer and knows nothing about where it came from.
/// </para>
/// </remarks>
public sealed partial class OverviewViewModel : ObservableObject, IDashboardPage, IDisposable
{
    private readonly IUsageHistoryService _history;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;
    private bool _loading;

    [ObservableProperty]
    private string _greetingText;

    [ObservableProperty]
    private string _eyebrowText;

    [ObservableProperty]
    private bool _hasVisibleProviders;

    [ObservableProperty]
    private bool _hasActivity;

    [ObservableProperty]
    private string _statusLine = UsageOverview.Empty.StatusLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIsNeutral))]
    private bool _statusIsOk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIsNeutral))]
    private bool _statusIsError;

    /// <summary>Initializes the page over the dashboard's provider rows.</summary>
    /// <param name="providers">The rows, shared with the provider pages.</param>
    /// <param name="history">The store the usage history panel and pacing read.</param>
    /// <param name="timeProvider">The clock the greeting and the comparisons are read from.</param>
    public OverviewViewModel(
        IEnumerable<ProviderViewModel> providers,
        IUsageHistoryService history,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _history = history;
        _timeProvider = timeProvider;
        _greetingText = Greeting.Now(timeProvider);
        _eyebrowText = Eyebrow(_greetingText);

        foreach (ProviderViewModel provider in providers)
        {
            provider.PropertyChanged += OnProviderPropertyChanged;
            Providers.Add(provider);
        }

        // Over every provider, not the carded ones: a provider uninstalled today still used
        // something last week, and dropping its line would erase the past rather than tidy
        // the present.
        UsageHistory = new HistoryViewModel(Providers, history, timeProvider)
        {
            SelectedRange = HistoryRange.Week,
        };

        RebuildVisibleProviders();
        RebuildStatus();
    }

    /// <inheritdoc />
    public string Title => "Overview";

    /// <summary>The page's own name, as the heading reads it.</summary>
    public string PageTitle => "Usage overview";

    /// <summary>
    /// Every provider the dashboard was built over, installed here or not. This is the set
    /// that is read, paced, charted and aggregated into the status line; it is not the set
    /// that gets a card.
    /// </summary>
    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    /// <summary>
    /// One card per provider that is actually on this machine. The rule, and the reasons
    /// each status falls where it does, are <see cref="ProviderVisibility"/>'s.
    /// </summary>
    /// <remarks>
    /// A card for a provider that is not installed is a name, a mark, no dial and a sentence
    /// saying nothing was reported, which is the same clutter the tray panel had. A card for
    /// a provider whose reading failed is a name, a sentence and a Retry, which is the
    /// opposite: it is the page telling you something is wrong and offering the one action
    /// that can change it. Only the first is dropped.
    /// </remarks>
    public ObservableCollection<ProviderViewModel> VisibleProviders { get; } = [];

    /// <summary>The last week of history, as the panel beside the activity list shows it.</summary>
    public HistoryViewModel UsageHistory { get; }

    /// <summary>Every provider's live sessions, most recently active first.</summary>
    public ObservableCollection<AgentSessionViewModel> Activity { get; } = [];

    /// <summary>The line under the title.</summary>
    public string SubHeading => Greeting.SubHeading;

    /// <summary>The activity panel's heading.</summary>
    public string ActivityTitle => "Agent activity";

    /// <summary>
    /// The badge on the activity panel. It is a label rather than a picker: activity is read
    /// live and never written down, so there is no other span to offer.
    /// </summary>
    /// <remarks>
    /// <b>"Live" means the panel follows every reading</b>, which is the same cadence the
    /// cards, the status line and the tray panel move at. It used to mean "read once, when
    /// the window opened": an agent that started and finished while somebody sat on this page
    /// never appeared on it, under a badge saying it would. Following the readings costs
    /// nothing extra - a provider's sessions are read by the same pass that reads its usage,
    /// and asking for them again hands back what that pass already has.
    /// </remarks>
    public string LiveLabel => "Live";

    /// <summary>
    /// The follow-up to the most recent reading: the session list it brought and the pacing
    /// comparison it invalidated. A completed task when no reading is being followed.
    /// </summary>
    /// <remarks>
    /// A reading arrives on an event, so the work it starts is not awaited by whoever caused
    /// it, the same way a settings change writes itself. This is the handle on it, for a
    /// caller that needs to know the panel has caught up.
    /// </remarks>
    public Task FollowingReading { get; private set; } = Task.CompletedTask;

    /// <summary>Shown when no provider reports a live session.</summary>
    public string NoActivityText => UsageFormat.NoActivity;

    /// <summary>
    /// Shown in place of the cards when none of the providers is installed here. It is the
    /// documented sentence for that state rather than an empty grid, and it is the same one
    /// the status line arrives at over the same set, so the two cannot disagree.
    /// </summary>
    public string NoProvidersText => UsageFormat.NoProviders;

    /// <summary>Whether the status dot carries no status colour at all.</summary>
    public bool StatusIsNeutral => !StatusIsOk && !StatusIsError;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken ct)
    {
        GreetingText = Greeting.Now(_timeProvider);
        EyebrowText = Eyebrow(GreetingText);

        // Every reading announces itself, and the announcement starts the follow-up that
        // reads that provider's sessions and repaces it. The load used to do both jobs as
        // well, which put two writers on one session list with nothing deciding their order:
        // the activity panel could be rebuilt from a list another task was halfway through
        // clearing. So the load takes the readings, holds the follow-ups off while it does,
        // and then runs them itself, one provider at a time.
        List<Task> readings = [];
        _loading = true;
        try
        {
            foreach (ProviderViewModel provider in Providers)
            {
                readings.Add(provider.LoadAsync(ct));
            }

            await Task.WhenAll(readings).ConfigureAwait(true);
        }
        finally
        {
            _loading = false;
        }

        foreach (ProviderViewModel provider in Providers)
        {
            await FollowAsync(provider, ct).ConfigureAwait(true);
        }

        await UsageHistory.LoadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Stops listening to the provider rows. It does not own them.</summary>
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
        }
    }

    /// <summary>The greeting as the eyebrow sets it: the one capitalised string in Altim.</summary>
    private static string Eyebrow(string greeting) => greeting.ToUpper(CultureInfo.CurrentCulture);

    /// <summary>
    /// Asks history what this provider's paced window was standing at one window ago, and
    /// hands the row the difference. A provider with no usable figure, or with too little
    /// history behind it, is handed a null and renders an em dash.
    /// </summary>
    /// <remarks>
    /// Run again after every reading, and that is the point of it being separable. A reading
    /// rebuilds the row's metrics, and the row drops the comparison as it does so because the
    /// figure it was computed from has just moved. Nothing then put a new one back: pacing was
    /// restored only by the page's own load, which happens once when the window is built, so
    /// every card fell to an em dash on the first refresh and stayed there for as long as the
    /// window was open. That em dash means "not enough local history to compare", which was
    /// false - the history was there and the comparison had been made a minute earlier.
    /// </remarks>
    private async Task LoadPacingAsync(ProviderViewModel provider, CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (provider.PacingMetric is not { Value: { } current } metric
            || UsagePacing.ComparisonInstant(metric.Window, now) is not { } at)
        {
            provider.ApplyPacing(null);
            return;
        }

        IReadOnlyList<UsageSample> carryIn;
        try
        {
            carryIn = await BackgroundWork.RunAsync(
                token => _history.GetLatestBeforeAsync(provider.Id, at, token), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // A history read that fails is a comparison that cannot be made, which is
            // exactly what the em dash says. It is not a provider failure.
            provider.ApplyPacing(null);
            return;
        }

        provider.ApplyPacing(UsagePacing.Compare(carryIn, metric.Key, current, at));
    }

    /// <summary>Rebuilds the activity list from every listed provider's sessions.</summary>
    /// <remarks>
    /// Left alone when nothing a reader can see has changed, which is the same discipline
    /// <see cref="RebuildVisibleProviders"/> keeps and for the same reason: this now runs on
    /// every reading from every provider, and emptying an observable collection and refilling
    /// it throws away the list's item containers. A panel that rebuilt itself every ten
    /// seconds would blink, and would throw away the reader's scroll position while they were
    /// looking at it. The rows are snapshots, so a row is compared by what it prints rather
    /// than by reference: a new instance carrying the same words is the same row.
    /// </remarks>
    private void RebuildActivity()
    {
        List<AgentSessionViewModel> sessions = [];
        foreach (ProviderViewModel provider in VisibleProviders)
        {
            sessions.AddRange(provider.Sessions);
        }

        sessions.Sort(static (left, right) => right.LastActivityAt.CompareTo(left.LastActivityAt));

        if (Reads(Activity, sessions))
        {
            return;
        }

        Activity.Clear();
        foreach (AgentSessionViewModel session in sessions)
        {
            Activity.Add(session);
        }

        HasActivity = Activity.Count > 0;
    }

    /// <summary>Whether two activity lists put the same thing on screen.</summary>
    /// <param name="current">The list as it stands.</param>
    /// <param name="wanted">The list the latest readings produce.</param>
    private static bool Reads(
        IReadOnlyList<AgentSessionViewModel> current,
        IReadOnlyList<AgentSessionViewModel> wanted)
    {
        if (current.Count != wanted.Count)
        {
            return false;
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if (!string.Equals(current[i].Id, wanted[i].Id, StringComparison.Ordinal)
                || !string.Equals(current[i].ProviderName, wanted[i].ProviderName, StringComparison.Ordinal)
                || !string.Equals(current[i].Title, wanted[i].Title, StringComparison.Ordinal)
                || !string.Equals(current[i].DetailText, wanted[i].DetailText, StringComparison.Ordinal)
                || !string.Equals(current[i].RelativeText, wanted[i].RelativeText, StringComparison.Ordinal)
                || current[i].IsActive != wanted[i].IsActive)
            {
                return false;
            }
        }

        return true;
    }

    private void OnProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ProviderViewModel.CurrentUsage)
            || sender is not ProviderViewModel provider
            || _disposed)
        {
            return;
        }

        RebuildVisibleProviders();
        RebuildStatus();

        // The page's own load runs the follow-ups itself, in order, once the readings are in.
        if (_loading)
        {
            return;
        }

        FollowingReading = FollowAsync(provider);
    }

    /// <summary>
    /// Brings the two things a reading changes that the reading itself does not carry up to
    /// date: what that provider is running now, and how this window compares with the last.
    /// </summary>
    /// <param name="provider">The row whose reading just landed.</param>
    /// <param name="ct">
    /// Cancels the follow-up. The page's own load passes its token; a reading that arrived on
    /// the provider's event has none to pass, and stops on <see cref="_disposed"/> instead.
    /// </param>
    /// <remarks>
    /// Neither half throws: a session read that fails empties that provider's list, and a
    /// history read that fails is an em dash.
    /// </remarks>
    private async Task FollowAsync(ProviderViewModel provider, CancellationToken ct = default)
    {
        await provider.LoadSessionsAsync(ct).ConfigureAwait(true);

        if (_disposed)
        {
            return;
        }

        RebuildActivity();
        await LoadPacingAsync(provider, ct).ConfigureAwait(true);
    }

    /// <summary>Rebuilds the carded set from the registered one, in registration order.</summary>
    /// <remarks>
    /// Left alone when the answer has not changed, which is almost every time: this runs on
    /// every reading from every provider, and replacing the collection each time would throw
    /// the cards away and rebuild them, dials and all, for nothing.
    /// </remarks>
    private void RebuildVisibleProviders()
    {
        List<ProviderViewModel> shown = [];
        foreach (ProviderViewModel provider in Providers)
        {
            if (provider.IsShown)
            {
                shown.Add(provider);
            }
        }

        HasVisibleProviders = shown.Count > 0;

        if (VisibleProviders.Count == shown.Count)
        {
            bool same = true;
            for (int i = 0; i < shown.Count; i++)
            {
                if (!ReferenceEquals(VisibleProviders[i], shown[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        VisibleProviders.Clear();
        foreach (ProviderViewModel provider in shown)
        {
            VisibleProviders.Add(provider);
        }
    }

    /// <summary>
    /// The one sentence about the integrations as a whole, over <b>every</b> provider rather
    /// than over the carded ones. It is what explains a page with fewer cards than the
    /// machine has agents, and what the page says when there are no cards at all.
    /// </summary>
    private void RebuildStatus()
    {
        List<ProviderUsage> readings = [];
        foreach (ProviderViewModel provider in Providers)
        {
            readings.Add(provider.CurrentUsage);
        }

        UsageOverview overview = UsageAggregator.Aggregate(readings, DisplayNameFor);
        StatusLine = overview.StatusLine;
        StatusIsError = overview.Status == ProviderStatus.Error;
        StatusIsOk = overview.Status is ProviderStatus.Active or ProviderStatus.Idle or ProviderStatus.Detected;
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
}
