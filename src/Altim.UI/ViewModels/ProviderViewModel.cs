using System.Collections.ObjectModel;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.Core.Usage;
using Altim.UI.Formatting;
using Altim.UI.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Altim.UI.ViewModels;

/// <summary>
/// One provider, as every surface shows it: the popup row, the Overview card and the
/// provider page all bind to the same instance.
/// </summary>
/// <remarks>
/// <para>
/// The view model is built from <see cref="IUsageProvider"/> alone. It never sees a concrete
/// provider, a storage type or a platform service, which is what lets a new provider appear in
/// the interface without a view changing.
/// </para>
/// <para>
/// Nothing is read in the constructor. A reading is taken by <see cref="LoadAsync"/>, pushed in
/// by <see cref="Apply"/>, or delivered by the provider's own <c>UsageChanged</c> event, and all
/// three land on the dispatcher thread. A failed reading becomes the error copy and an enabled
/// retry, never an exception and never a zero.
/// </para>
/// </remarks>
public sealed partial class ProviderViewModel : ObservableObject, IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly TimeProvider _timeProvider;
    private AltimSettings _settings;
    private bool _disposed;

    [ObservableProperty]
    private StatusDisplay _status = StatusDisplay.Unknown;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    [NotifyPropertyChangedFor(nameof(ShowsNoMetricsNotice))]
    [NotifyPropertyChangedFor(nameof(ShowsNoFiguresNotice))]
    private bool _hasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNoMetricsNotice))]
    private bool _hasMetrics;

    [ObservableProperty]
    private string? _tokensText;

    [ObservableProperty]
    private bool _hasTokens;

    [ObservableProperty]
    private string _lastRefreshedText = UsageFormat.NotRefreshedYet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReset))]
    private string? _resetText;

    [ObservableProperty]
    private string _resetRemainingText = UsageFormat.Unknown;

    [ObservableProperty]
    private string _integrationText = UsageFormat.IntegrationSentence(ProviderStatus.Unknown);

    [ObservableProperty]
    private bool _hasSessions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private ProviderUsage _currentUsage;

    [ObservableProperty]
    private string _pacingText = UsageFormat.PacingUnknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPacingCaption))]
    private string? _pacingCaption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPacingBetter))]
    private double? _pacingDelta;

    /// <summary>Initializes a view model over one provider.</summary>
    /// <param name="provider">The provider to report on.</param>
    /// <param name="timeProvider">The clock reset and refresh times are measured against.</param>
    /// <param name="settings">Supplies the thresholds the meters tick at.</param>
    public ProviderViewModel(IUsageProvider provider, TimeProvider timeProvider, AltimSettings settings)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(settings);

        _provider = provider;
        _timeProvider = timeProvider;
        _settings = settings;

        Id = provider.Id;
        DisplayName = provider.DisplayName;
        IsAnthropic = ProviderIdentity.IsAnthropic(provider.Id);
        IsOpenAI = ProviderIdentity.IsOpenAI(provider.Id);

        _currentUsage = new ProviderUsage(provider.Id, provider.Status, [], null, null, null);
        _status = new StatusDisplay(provider.Status);
        _integrationText = UsageFormat.IntegrationSentence(provider.Status);

        provider.UsageChanged += OnUsageChanged;
    }

    /// <summary>Raised when the row asks for the provider's own page.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>The provider's identifier.</summary>
    public string Id { get; }

    /// <summary>The provider's name, as shown.</summary>
    public string DisplayName { get; }

    /// <summary>Whether the mark wears the Anthropic accent.</summary>
    public bool IsAnthropic { get; }

    /// <summary>Whether the mark wears the OpenAI accent.</summary>
    public bool IsOpenAI { get; }

    /// <summary>The metrics this provider actually reports, in the order it reports them.</summary>
    public ObservableCollection<MetricViewModel> Metrics { get; } = [];

    /// <summary>
    /// The same metrics as the one line the tray panel has room for, and only the ones
    /// carrying a figure: a window with nothing to report is left out of the line rather
    /// than shown with a dash, because the line is a summary and a summary of nothing is
    /// not a summary. When this is empty the panel shows a sentence instead.
    /// </summary>
    public ObservableCollection<CompactMetricViewModel> CompactMetrics { get; } = [];

    /// <summary>The four token counts, present only when the provider reports tokens at all.</summary>
    public ObservableCollection<TokenRowViewModel> TokenRows { get; } = [];

    /// <summary>The provider's live agent sessions, most recently active first.</summary>
    public ObservableCollection<AgentSessionViewModel> Sessions { get; } = [];

    /// <summary>The sentence shown in place of the metrics when a reading fails.</summary>
    public string ErrorText => UsageFormat.ProviderUnavailable;

    /// <summary>The sentence shown when a reading succeeds but carries no metric.</summary>
    public string NoMetricsText => UsageFormat.MetricUnavailable;

    /// <summary>The sentence shown when a provider reports no live session.</summary>
    public string NoActivityText => UsageFormat.NoActivity;

    /// <summary>The label on the action that retries a failed reading.</summary>
    public string RetryLabel => UsageFormat.RetryLabel;

    /// <summary>
    /// Whether the reading succeeded and carried no metric at all, which is a different
    /// thing from a reading that failed and is said differently.
    /// </summary>
    public bool ShowsNoMetricsNotice => !HasError && !HasMetrics;

    /// <summary>Whether any window this provider reports has a reset instant to show.</summary>
    public bool HasReset => ResetText is not null;

    /// <summary>Whether the panel's one line has anything to put on it.</summary>
    public bool HasCompactMetrics => CompactMetrics.Count > 0;

    /// <summary>
    /// Whether the panel shows the unavailable sentence in place of its one line. That is
    /// true both when the reading carried no metric at all and when it carried metrics that
    /// none of them reported a figure for: on a surface with one line per provider the two
    /// are the same thing to a reader, and the alternative is a provider row with a name and
    /// nothing under it.
    /// </summary>
    public bool ShowsNoFiguresNotice => !HasError && CompactMetrics.Count == 0;

    /// <summary>
    /// The metric pacing is measured on: the shortest window that reports a figure. It is
    /// the window that moves within a day, so it is the one a comparison says anything
    /// about. Null when this provider reports no usable figure at all.
    /// </summary>
    public MetricViewModel? PacingMetric { get; private set; }

    /// <summary>Whether a caption naming what pacing was compared against exists.</summary>
    public bool HasPacingCaption => PacingCaption is not null;

    /// <summary>
    /// Whether this window is running below the one before it. Pacing above the previous
    /// window is left in the ordinary ink: the reference colours the good news and leaves
    /// the rest alone, and a red figure here would be a threshold warning the meter has
    /// not actually reached.
    /// </summary>
    public bool IsPacingBetter => PacingDelta is { } delta && delta < 0d;

    /// <summary>The label over the pacing figure.</summary>
    public string PacingLabel => UsageFormat.PacingLabel;

    /// <summary>The label over the reset time.</summary>
    public string ResetsLabel => UsageFormat.ResetsLabel;

    /// <summary>Takes a reading, off the dispatcher thread, and applies it.</summary>
    /// <param name="ct">Cancels the reading.</param>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ProviderUsage usage = await BackgroundWork.RunAsync(_provider.GetUsageAsync, ct)
                .ConfigureAwait(true);
            Apply(usage);
        }
        catch (OperationCanceledException)
        {
            // A cancelled reading is not a failure: the surface closed, or a newer reading started.
        }
        catch (Exception)
        {
            ApplyFailure();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reads the provider's live sessions, off the dispatcher thread.</summary>
    /// <param name="ct">Cancels the read.</param>
    public async Task LoadSessionsAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            IReadOnlyList<AgentSession> sessions =
                await BackgroundWork.RunAsync(_provider.GetSessionsAsync, ct).ConfigureAwait(true);
            ApplySessions(sessions);
        }
        catch (OperationCanceledException)
        {
            // See LoadAsync.
        }
        catch (Exception)
        {
            // Activity is a secondary reading. Losing it says nothing about usage, so the
            // list empties and the page shows its own sentence rather than an error.
            ApplySessions([]);
        }
    }

    /// <summary>Applies a reading taken elsewhere, such as by the monitor scheduler.</summary>
    /// <param name="usage">The reading to show.</param>
    public void Apply(ProviderUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        // A window that has already rolled over carries a percentage that describes a window
        // nobody is in any more. Core drops it rather than showing it as current, and the
        // metric reads as unreported until the next reading arrives.
        usage = UsageReadingNormaliser.Normalise(usage, _timeProvider.GetUtcNow());

        Status = new StatusDisplay(usage.Status);
        HasError = usage.Status == ProviderStatus.Error;
        IntegrationText = UsageFormat.IntegrationSentence(usage.Status);
        LastRefreshedText = UsageFormat.LastRefreshed(usage.LastRefreshed);

        RebuildMetrics(usage);
        RebuildTokens(usage);

        // Announced last, and deliberately: the panel rebuilds its reset list and its status
        // line when this changes, and a reading announced before the rows were rebuilt would
        // have it reading the previous reading's metrics.
        CurrentUsage = usage;
    }

    /// <summary>
    /// Applies a pacing comparison computed from local history.
    /// </summary>
    /// <param name="deltaPercent">
    /// This window's level less the previous window's level at the same point in it, in
    /// percentage points, or <see langword="null"/> when there is not enough history to
    /// compare. A null reads as an em dash, never as a zero.
    /// </param>
    public void ApplyPacing(double? deltaPercent)
    {
        PacingDelta = deltaPercent;
        PacingText = UsageFormat.Pacing(deltaPercent);
    }

    /// <summary>Applies new thresholds without taking a fresh reading.</summary>
    /// <param name="settings">The settings the meters tick against.</param>
    public void ApplySettings(AltimSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        RebuildMetrics(CurrentUsage);
    }

    /// <summary>Stops listening to the provider.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _provider.UsageChanged -= OnUsageChanged;
    }

    [RelayCommand]
    private void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);

    private bool CanRetry => HasError && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await BackgroundWork.RunAsync(_provider.RefreshAsync, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            ApplyFailure();
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync(ct).ConfigureAwait(true);
    }

    private void OnUsageChanged(object? sender, ProviderUsage usage) =>
        UiThread.Post(() =>
        {
            if (!_disposed)
            {
                Apply(usage);
            }
        });

    // The sentence is the contract's, never the exception's. An exception message is a
    // developer's sentence: it can name a path, a command or a token, and MonitorScheduler
    // routes the exception itself to ProviderFailed for logging instead.
    private void ApplyFailure() => Apply(new ProviderUsage(
        Id,
        ProviderStatus.Error,
        [],
        null,
        CurrentUsage.LastRefreshed,
        ProviderUsage.UnavailableDetail));

    private void RebuildMetrics(ProviderUsage usage)
    {
        Metrics.Clear();

        // A failed reading says nothing about usage, so it carries no metric rows at all.
        // UsageAggregator takes the same position, and a stale row beside an error would
        // read as a current number.
        if (usage.Status != ProviderStatus.Error)
        {
            for (int i = 0; i < usage.Metrics.Count; i++)
            {
                Metrics.Add(new MetricViewModel(usage.Metrics[i], _settings, _timeProvider, i == 0));
            }
        }

        HasMetrics = Metrics.Count > 0;
        RebuildCompactMetrics();
        RebuildReset();
        RebuildPacingMetric();
    }

    private void RebuildCompactMetrics()
    {
        CompactMetrics.Clear();

        foreach (MetricViewModel metric in Metrics)
        {
            if (metric.PercentText is not { } percent)
            {
                continue;
            }

            CompactMetrics.Add(new CompactMetricViewModel(
                UsageFormat.WindowShort(metric.Window) ?? metric.Label,
                percent,
                CompactMetrics.Count > 0));
        }

        OnPropertyChanged(nameof(HasCompactMetrics));
        OnPropertyChanged(nameof(ShowsNoFiguresNotice));
    }

    /// <summary>
    /// Picks the window pacing is measured on, and drops the comparison that stood beside
    /// the previous reading: the figure was computed against numbers that have just moved.
    /// </summary>
    private void RebuildPacingMetric()
    {
        MetricViewModel? shortest = null;
        foreach (MetricViewModel metric in Metrics)
        {
            if (metric.Value is null || metric.Window is not { } window || window.Length <= TimeSpan.Zero)
            {
                continue;
            }

            if (shortest?.Window is not { } best || window.Length < best.Length)
            {
                shortest = metric;
            }
        }

        PacingMetric = shortest;
        PacingCaption = UsageFormat.PacingCaption(shortest?.Window);
        ApplyPacing(null);
    }

    private void RebuildReset()
    {
        MetricViewModel? soonest = null;
        foreach (MetricViewModel metric in Metrics)
        {
            if (metric.ResetsAt is not { } instant)
            {
                continue;
            }

            if (soonest?.ResetsAt is not { } best || instant < best)
            {
                soonest = metric;
            }
        }

        ResetText = soonest?.ResetText;
        ResetRemainingText = soonest?.RemainingText ?? UsageFormat.Unknown;
    }

    private void RebuildTokens(ProviderUsage usage)
    {
        TokenRows.Clear();
        TokensText = UsageFormat.Tokens(usage.Tokens);
        HasTokens = TokensText is not null;

        if (usage.Tokens is not { } tokens)
        {
            return;
        }

        TokenRows.Add(new TokenRowViewModel("Input", tokens.Input));
        TokenRows.Add(new TokenRowViewModel("Output", tokens.Output));
        TokenRows.Add(new TokenRowViewModel("Cache read", tokens.CacheRead));
        TokenRows.Add(new TokenRowViewModel("Cache write", tokens.CacheWrite));
    }

    private void ApplySessions(IReadOnlyList<AgentSession> sessions)
    {
        Sessions.Clear();

        List<AgentSession> ordered = [.. sessions];
        ordered.Sort(static (left, right) =>
            (right.LastActivityAt ?? right.StartedAt).CompareTo(left.LastActivityAt ?? left.StartedAt));

        foreach (AgentSession session in ordered)
        {
            Sessions.Add(new AgentSessionViewModel(session, DisplayName, _timeProvider));
        }

        HasSessions = Sessions.Count > 0;
    }
}
