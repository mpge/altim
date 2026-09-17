using System.Collections.ObjectModel;
using System.Globalization;
using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.UI.Controls;
using Altim.UI.Formatting;
using Altim.UI.History;
using Altim.UI.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Altim.UI.ViewModels;

/// <summary>
/// The history page: one line per provider over 24 hours, 7 days or 30 days.
/// </summary>
/// <remarks>
/// <para>
/// A range with no samples is an ordinary outcome, not an error. Altim writes a sample only when
/// a value changes, so a machine that ran no agent last week has nothing to draw. The page hands
/// the tape an empty series list, and the tape says so in one sentence instead of drawing a flat
/// line along zero.
/// </para>
/// <para>
/// Reads go to the thread pool because the history service opens SQLite, and a generation counter
/// drops the result of a query the user has already navigated away from.
/// </para>
/// </remarks>
public sealed partial class HistoryViewModel : ObservableObject, IDashboardPage
{
    private readonly IUsageHistoryService _history;
    private readonly TimeProvider _timeProvider;
    private readonly List<ProviderViewModel> _providers;
    private int _generation;

    [ObservableProperty]
    private HistoryRange _selectedRange = HistoryRange.Day;

    [ObservableProperty]
    private IReadOnlyList<UsageTapeSeries> _series = [];

    [ObservableProperty]
    private IReadOnlyList<string> _axisLabels = [];

    [ObservableProperty]
    private bool _hasSamples;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Initializes the page.</summary>
    /// <param name="providers">The providers whose history is drawn.</param>
    /// <param name="history">The store the samples come from.</param>
    /// <param name="timeProvider">The clock the range is measured back from.</param>
    public HistoryViewModel(
        IEnumerable<ProviderViewModel> providers,
        IUsageHistoryService history,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _providers = [.. providers];
        _history = history;
        _timeProvider = timeProvider;
        Map = new UsageMapViewModel(_providers, history, timeProvider);
    }

    /// <inheritdoc />
    public string Title => "History";

    /// <summary>
    /// The calendar above the tape: a year of daily totals, one square per day per provider.
    /// </summary>
    /// <remarks>
    /// The map answers how much and when, over a year; the tape answers what a level did
    /// during a day or a month. They read the same store and are loaded together, but the map
    /// does not follow the range picker: its span is a year and the picker moves the tape.
    /// </remarks>
    public UsageMapViewModel Map { get; }

    /// <summary>The panel's own heading, wherever the tape is shown inside one.</summary>
    public string PanelTitle => "Usage history";

    /// <summary>The line under the page title.</summary>
    public string SubHeading => "How much each provider has used over time.";

    /// <summary>The three spans on offer.</summary>
    public IReadOnlyList<HistoryRange> Ranges => HistoryRange.All;

    /// <summary>
    /// One entry per line on the tape, naming it. The reference names the lines above the
    /// plot rather than at their ends, which is the arrangement that survives two lines
    /// finishing at the same level.
    /// </summary>
    public ObservableCollection<HistoryLegendItem> Legend { get; } = [];

    /// <summary>The sentence shown in place of a chart when the range holds no sample.</summary>
    public string EmptyText => UsageFormat.HistoryEmpty;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken ct)
    {
        int generation = ++_generation;
        HistoryRange range = SelectedRange;
        IsLoading = true;

        // Started before the tape's own reads rather than after them, so the two queries
        // overlap instead of the page waiting out a year of days and then a range of samples.
        Task map = Map.LoadAsync(ct);

        try
        {
            DateTimeOffset to = _timeProvider.GetUtcNow();
            DateTimeOffset from = to - range.Length;
            List<UsageTapeSeries> series = [];

            foreach (ProviderViewModel provider in _providers)
            {
                IReadOnlyList<UsageSample> inRange = await ReadAsync(
                    token => _history.GetRangeAsync(provider.Id, from, to, token), ct).ConfigureAwait(true);

                // Rows are written only when a value changes, so a range can legitimately hold
                // no row while the level is well known. The carry-in is the last row before the
                // range, and it is what draws the left edge.
                IReadOnlyList<UsageSample> carryIn = await ReadAsync(
                    token => _history.GetLatestBeforeAsync(provider.Id, from, token), ct).ConfigureAwait(true);

                if (generation != _generation)
                {
                    return;
                }

                if ((UsageHistorySeries.SelectPrimaryMetricKey(inRange)
                    ?? UsageHistorySeries.SelectPrimaryMetricKey(carryIn)) is not { } metricKey)
                {
                    continue;
                }

                List<UsageSample> forMetric =
                [
                    .. UsageHistorySeries.ForMetric(carryIn, metricKey),
                    .. UsageHistorySeries.ForMetric(inRange, metricKey),
                ];

                IReadOnlyList<double?> values = UsageHistorySeries.Build(forMetric, from, to, range.Buckets);

                if (values.Count == 0)
                {
                    continue;
                }

                series.Add(new UsageTapeSeries(
                    provider.DisplayName,
                    values,
                    series.Count == 0 ? UsageTapeEmphasis.Primary : UsageTapeEmphasis.Secondary));
            }

            Series = series;
            HasSamples = series.Count > 0;
            AxisLabels = series.Count > 0 ? Ticks(from, to, range) : [];

            Legend.Clear();
            foreach (UsageTapeSeries line in series)
            {
                Legend.Add(new HistoryLegendItem(
                    line.Name,
                    line.Emphasis == UsageTapeEmphasis.Secondary));
            }
        }
        finally
        {
            // Awaited however the tape's own load ended, so navigating away part way through
            // does not leave a year of days in flight with nobody watching it.
            await map.ConfigureAwait(true);

            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// The dates written along the bottom of the plot, evenly spaced from the left edge to
    /// the right. They are read in local time, because the span they describe is the user's
    /// day rather than UTC's.
    /// </summary>
    private static IReadOnlyList<string> Ticks(DateTimeOffset from, DateTimeOffset to, HistoryRange range)
    {
        if (range.Ticks < 2)
        {
            return [];
        }

        List<string> labels = new(range.Ticks);
        for (int i = 0; i < range.Ticks; i++)
        {
            DateTimeOffset at = from + ((to - from) * i / (range.Ticks - 1));
            labels.Add(at.ToLocalTime().ToString(range.TickFormat, CultureInfo.CurrentCulture));
        }

        return labels;
    }

    private static async Task<IReadOnlyList<UsageSample>> ReadAsync(
        Func<CancellationToken, ValueTask<IReadOnlyList<UsageSample>>> query,
        CancellationToken ct)
    {
        try
        {
            return await BackgroundWork.RunAsync(query, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception)
        {
            // A history read that fails leaves the provider without a line. The page then
            // shows the empty sentence, which is true: there is nothing to draw.
            return [];
        }
    }

    partial void OnSelectedRangeChanged(HistoryRange value)
    {
        _ = value;
        _ = LoadAsync(CancellationToken.None);
    }
}
