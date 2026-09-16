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
    }

    /// <inheritdoc />
    public string Title => "History";

    /// <summary>The three spans on offer.</summary>
    public IReadOnlyList<HistoryRange> Ranges => HistoryRange.All;

    /// <summary>The sentence shown in place of a chart when the range holds no sample.</summary>
    public string EmptyText => UsageFormat.HistoryEmpty;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken ct)
    {
        int generation = ++_generation;
        HistoryRange range = SelectedRange;
        IsLoading = true;

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
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
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
