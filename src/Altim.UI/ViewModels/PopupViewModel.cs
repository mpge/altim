using System.Collections.ObjectModel;
using System.ComponentModel;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.Core.Usage;
using Altim.UI.Controls;
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
    private bool _hasVisibleProviders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeadline))]
    private HeadlineReadingViewModel? _headline;

    [ObservableProperty]
    private IReadOnlyList<DialReading> _dialReadings = [];

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

        Rebuild();
    }

    /// <summary>
    /// Raised when the panel asks for the dashboard. The payload is the sidebar section to
    /// open it on, or null for wherever it opens by default: the gear asks for Settings and a
    /// provider's own row asks for that provider, and the panel does not open windows itself.
    /// </summary>
    public event EventHandler<string?>? OpenRequested;

    /// <summary>
    /// One row per provider, in the order they were registered, whether or not the machine
    /// has that provider. This is the set that is read, aggregated into the status line and
    /// disposed; it is not the set that is shown.
    /// </summary>
    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    /// <summary>
    /// The providers the panel actually lists: every registered one except those settled as
    /// not installed on this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProviderVisibility"/> holds the rule and says why each status falls where
    /// it does. The short version is that a provider which is simply not here has nothing to
    /// report, and one whose last reading failed has something important to report, so
    /// absence is hidden and failure never is.
    /// </para>
    /// <para>
    /// A separate collection rather than a flag each row hides itself with, because
    /// everything else on the panel is built by walking this list: the rings on the dial,
    /// the legend that names them and the reset lines. A row that hid itself would leave its
    /// ring on the face and its line under the resets heading, which is worse than the
    /// clutter it was removing - a face carrying an arc for a provider the panel does not
    /// list.
    /// </para>
    /// </remarks>
    public ObservableCollection<ProviderViewModel> VisibleProviders { get; } = [];

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

    /// <summary>
    /// The accessible name of the dial. The face is drawn, carries a sweep per provider and
    /// has no one window to be named after, so this says what it is and its own peer reads
    /// out every reading on it afterwards.
    /// </summary>
    public string DialLabel => "Usage by provider";

    /// <summary>
    /// The accessible name of the legend beside the dial. Its rows carry their own readings,
    /// so this only has to say what the list is: the names of the arcs on the face.
    /// </summary>
    public string LegendLabel => "Rings on the dial";

    /// <summary>Whether the status icon reads as operational.</summary>
    public bool StatusIsOk => !StatusIsError;

    /// <summary>The name of the sidebar section the gear opens.</summary>
    public const string SettingsSection = "Settings";

    /// <summary>Shown in place of the resets section when no window reports an instant.</summary>
    public string NoResetsText => UsageFormat.NoResetsReported;

    /// <summary>
    /// Shown in place of the provider rows when none of them is installed here. It is the
    /// documented sentence for that state rather than a blank panel, and it is the same one
    /// the status line arrives at over the same set, so the two cannot disagree.
    /// </summary>
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
        RebuildVisibleProviders();
        RebuildStatusLine();
        RebuildHeadline();
        RebuildResets();
    }

    /// <summary>Rebuilds the listed set from the registered one, in registration order.</summary>
    /// <remarks>
    /// Left alone when the answer has not changed, which is almost every time: the panel is
    /// rebuilt on every reading from every provider, and replacing the collection each time
    /// would throw away the list's item containers and make the rows flash for no reason.
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

        if (Same(VisibleProviders, shown))
        {
            return;
        }

        VisibleProviders.Clear();
        foreach (ProviderViewModel provider in shown)
        {
            VisibleProviders.Add(provider);
        }
    }

    private static bool Same(
        IReadOnlyList<ProviderViewModel> current,
        IReadOnlyList<ProviderViewModel> wanted)
    {
        if (current.Count != wanted.Count)
        {
            return false;
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if (!ReferenceEquals(current[i], wanted[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The one sentence about the integrations as a whole, over <b>every</b> provider rather
    /// than over the listed ones.
    /// </summary>
    /// <remarks>
    /// This is the line that explains a short list. On a machine with two of the three
    /// agents installed it reads "Gemini CLI not detected", which is the whole of what a
    /// reader needs to know about the one that is missing, and the reason a row, a ring and
    /// a reset line for it would be clutter rather than information. It is also what the
    /// panel says when nothing at all is installed: the aggregator answers that case with
    /// "No providers detected", which is the same sentence the empty provider section shows.
    /// </remarks>
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
    /// Builds the face: <b>one sweep per provider</b>, on its own concentric ring, and the
    /// one window the figure in the middle belongs to - the highest level any provider
    /// reports, the window nearest its ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is combined.</b> 33 per cent of one vendor's weekly allowance and 54 per
    /// cent of another's are proportions of two different, undisclosed limits: summing,
    /// averaging or token weighting them would produce an authoritative looking number that
    /// means nothing, and neither vendor publishes the allowance that would make one
    /// meaningful. The rings share a face because they share a <i>unit</i>, not because their
    /// figures have been put together. It is the same rule that makes the usage map's
    /// combined row sum tokens and never percentages.
    /// </para>
    /// <para>
    /// <b>The rings are in the order the providers were registered</b>, outermost first,
    /// which is the order the legend prints and the order the provider lines below stand in.
    /// Ordering them by level instead would make two rings swap places every time the numbers
    /// crossed, so the ring somebody had learned to read as one provider's would quietly
    /// become the other's; registration order does not move.
    /// </para>
    /// <para>
    /// Each ring carries that provider's own head window, chosen by the same rule an Overview
    /// card picks its dial's window by, so the figure in the middle is always one of the rings
    /// the legend names.
    /// </para>
    /// <para>
    /// The panel already prints every window's figure on its provider's own line, so the dial
    /// is not there to add a number. It is there to say which of those numbers decides
    /// whether you can keep working, and to be the one place the panel shows the configured
    /// threshold at all.
    /// </para>
    /// <para>
    /// The choosing rule itself is <see cref="HeadlineReadingViewModel.Beats"/>, which an
    /// Overview card runs over one provider's windows while the panel runs it over every
    /// provider's. What differs between the two surfaces is the set, not the rule, and a tie
    /// break written out twice is a tie break that drifts. Here the set is walked in the order
    /// the providers were registered, so the same readings always pick the same window.
    /// </para>
    /// <para>
    /// A window with no percentage is never picked over one that has a figure, but when no
    /// window anywhere reports one the panel still leads with the first window it has: the
    /// dial draws its unavailable face and the figure is an em dash, which says "nothing was
    /// reported for this" rather than leaving the panel headed by nothing. A panel with no
    /// windows at all has no headline and the section is not drawn - there would be no window
    /// to name, and an unnamed dial is furniture, and with no face to carry them there are no
    /// rings either.
    /// </para>
    /// <para>
    /// <b>Every listed provider gets a ring</b>, including one whose reading failed and one
    /// nothing has been read from yet. DESIGN.md: "a ring missing from the legend would be a
    /// provider missing from the panel" - and those two are providers the panel is still
    /// listing, with a row of their own under the face. The ring was previously taken from
    /// the provider's headline, which a provider with no metrics does not have, so on a
    /// machine with three providers and one unreachable the dial drew two rings and the
    /// legend named two, while three rows stood underneath. A provider with no window to name
    /// has its ring labelled with its own name, because there is no window to put in front
    /// of it, and the figure is the em dash every unreported reading gets.
    /// </para>
    /// </remarks>
    private void RebuildHeadline()
    {
        MetricViewModel? best = null;
        string? provider = null;
        List<DialReading> rings = [];

        foreach (ProviderViewModel row in VisibleProviders)
        {
            foreach (MetricViewModel metric in row.Metrics)
            {
                if (best is null || HeadlineReadingViewModel.Beats(metric, best))
                {
                    best = metric;
                    provider = row.DisplayName;
                }
            }

            // The ring number is taken from the list as it stands rather than from a
            // counter of its own, so the legend's marks and the face's rings cannot end up
            // numbered differently.
            rings.Add(row.Headline is { } head
                ? new DialReading(rings.Count, head.SourceLabel, head.Value, head.Threshold, head.ResetText)
                : new DialReading(rings.Count, row.DisplayName, null));
        }

        // Replaced rather than mutated: the dial caches its paths against the list it was
        // handed, so a collection edited in place would leave the face drawing the readings
        // before last. No headline is no face, and a face nothing draws carries no rings.
        DialReadings = best is null ? [] : rings;

        Headline = best is not null && provider is not null
            ? new HeadlineReadingViewModel(best, provider)
            : null;
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
        foreach (ProviderViewModel provider in VisibleProviders)
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
