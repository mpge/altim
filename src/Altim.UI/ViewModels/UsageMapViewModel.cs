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
/// A year of daily usage: one row of squares per provider, and a combined row under them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tokens sum and percentages do not.</b> Forty per cent of one vendor's weekly window and
/// forty per cent of another's are different quantities measured against different limits, so
/// adding them or averaging them would produce a figure nobody reported. The combined square
/// therefore carries no percentage at all, and the combined tooltip lists each provider's peak
/// on its own line.
/// </para>
/// <para>
/// <b>A day one provider has a figure for and another does not is known.</b> The combined
/// figure is then a total over the providers that knew the day rather than over all of them,
/// which is a different claim, so the tooltip names who the figure covers and who had
/// nothing. Without that, a day Codex was not installed for would read as a day Codex used
/// nothing.
/// </para>
/// <para>
/// <b>A square is known when its token figure is, and not when its row is.</b> A day may have
/// a row carrying a peak and no tokens — the ordinary shape of a day outside a provider's
/// backfill reach, because Altim's own readings are running totals and can never say what a
/// day spent. The square draws volume, so that day is an outline: we know how close to the
/// limit the user came and not how much they spent, and filling it at the foot of the ramp
/// would say the day cost nothing. The peak stays in the words beside the square, where it is
/// a fact rather than a fill.
/// </para>
/// <para>
/// <b>Silence and emptiness are different answers.</b> Nothing is read until
/// <see cref="LoadAsync"/> runs, and a store that could not be read leaves <see cref="Rows"/>
/// null, which the map draws as nothing at all. That holds on <i>every</i> load and not only
/// the first: a read that answered nothing clears whatever was drawn before, because a year
/// nothing can substantiate any more is not a year to leave on screen. Only a store that
/// answered and held no day produces rows of unknown squares, which is what makes the map say
/// <see cref="EmptyText"/>. Announcing an empty history on the strength of a read that failed
/// would be the same lie as painting an unknown day as a zero.
/// </para>
/// <para>
/// The whole build - a year per provider, a thousand cells, the words for each of them and the
/// quantile scale over the lot - happens on the thread pool. The History page is opened
/// repeatedly and none of that may land on the dispatcher.
/// </para>
/// </remarks>
public sealed partial class UsageMapViewModel : ObservableObject
{
    /// <summary>The label on the row that sums the providers above it.</summary>
    public const string CombinedName = "Combined";

    /// <summary>How many days the map covers, ending today.</summary>
    public const int DaysShown = 365;

    private readonly IUsageHistoryService _history;
    private readonly TimeProvider _timeProvider;
    private readonly List<ProviderViewModel> _providers;
    private int _generation;

    [ObservableProperty]
    private IReadOnlyList<UsageMapRow>? _rows;

    [ObservableProperty]
    private UsageMapRow? _combinedRow;

    [ObservableProperty]
    private UsageMapScale? _scale;

    /// <summary>Initializes the map.</summary>
    /// <param name="providers">The providers to draw a row for, in the order they are drawn.</param>
    /// <param name="history">The store the days come from.</param>
    /// <param name="timeProvider">The clock whose local day the map's right hand edge is.</param>
    public UsageMapViewModel(
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

    /// <summary>The panel's heading.</summary>
    public string PanelTitle => "Daily usage";

    /// <summary>
    /// The sentence shown in place of the grid when no provider has a single day. It is the
    /// one the product already documents rather than a second sentence saying the same thing
    /// about the same store.
    /// </summary>
    public string EmptyText => UsageFormat.HistoryEmpty;

    /// <summary>
    /// Reads a year of days per provider and rebuilds the map.
    /// </summary>
    /// <param name="ct">Cancels the load.</param>
    /// <remarks>
    /// <para>
    /// A generation counter drops the result of a load the page has already asked to redo, so
    /// two overlapping loads cannot leave the older one's picture on screen.
    /// </para>
    /// <para>
    /// <b>A read that answered nothing takes the picture down.</b> Returning early on a null
    /// left whatever was drawn before standing, which is only harmless on the very first
    /// load, when there is nothing there yet. On a range change, after the history is
    /// cleared, or on coming back to the page, it left last year on screen with nothing
    /// saying so - the product showing usage it can no longer substantiate, which is the one
    /// thing it is built not to do. Clearing is the honest answer and the documented one:
    /// null rows draw nothing at all, which says nothing is known, while the empty sentence
    /// stays reserved for a store that answered and held no day.
    /// </para>
    /// <para>
    /// A cancelled load is not that. Nobody asked it anything, so it leaves the picture
    /// alone, and so does a load a newer one has already superseded.
    /// </para>
    /// </remarks>
    public async Task LoadAsync(CancellationToken ct)
    {
        int generation = ++_generation;

        Snapshot? built;
        try
        {
            built = await BackgroundWork.RunAsync(BuildAsync, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _generation)
        {
            return;
        }

        if (built is null)
        {
            // Rows last here too, for the reason below: it is the one the map watches.
            Scale = null;
            CombinedRow = null;
            Rows = null;
            return;
        }

        Scale = built.Scale;
        CombinedRow = built.Combined;

        // Assigned last and assigned whole: the map watches this one, and it does not copy
        // what it is handed, so a list mutated in place would not repaint.
        Rows = built.Rows;
    }

    /// <summary>
    /// Reads every provider's year and turns it into rows, off the dispatcher thread.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// The picture to show, or <see langword="null"/> when not one provider's store could be
    /// read - in which case nothing is known, which is not the same as knowing there is
    /// nothing.
    /// </returns>
    private async ValueTask<Snapshot?> BuildAsync(CancellationToken ct)
    {
        DateOnly today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _timeProvider.LocalTimeZone).DateTime);
        DateOnly first = today.AddDays(-(DaysShown - 1));

        List<Read> reads = [];
        bool anyAnswered = false;

        foreach (ProviderViewModel provider in _providers)
        {
            Dictionary<DateOnly, UsageDay> days = [];

            try
            {
                days = Index(await _history.GetDaysAsync(provider.Id, first, today, ct).ConfigureAwait(false));
                anyAnswered = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // This provider's year could not be read, so it keeps a row of unknown days,
                // which is true. Whether every provider failed is what decides between a map
                // that says nothing and one that says the history is empty, and that is
                // answered by anyAnswered rather than by this row.
            }

            reads.Add(new Read(provider.DisplayName, days));
        }

        if (reads.Count > 0 && !anyAnswered)
        {
            return null;
        }

        List<long> values = [];
        List<UsageMapRow> rows = [];

        foreach (Read read in reads)
        {
            var cells = new UsageMapCell[DaysShown];
            for (int offset = 0; offset < DaysShown; offset++)
            {
                DateOnly day = first.AddDays(offset);

                // A row with a peak and no tokens is known about and is still an unknown
                // square: the square is the day's volume, and a peak says how close to the
                // limit the user came rather than what they spent. Filling it would claim the
                // day cost nothing. The words keep the peak, so nothing is lost by it.
                cells[offset] = read.Days.TryGetValue(day, out UsageDay? stored)
                    ? new UsageMapCell(day, stored.TotalTokens, stored.PeakPercent,
                                       IsKnown: stored.TotalTokens is not null,
                                       ProviderDetail(read.Name, stored))
                    : new UsageMapCell(day, null, null, IsKnown: false, UnknownDetail(day));

                if (cells[offset].Tokens is { } tokens)
                {
                    values.Add(tokens);
                }
            }

            rows.Add(new UsageMapRow(read.Name, cells));
        }

        // One provider's total is that provider's row, so a combined row under it would be a
        // second copy of the same year wearing a different name. The row exists to add
        // providers together, and there is nothing to add.
        UsageMapRow? combined = reads.Count > 1 ? Combine(reads, first, values) : null;
        if (combined is not null)
        {
            rows.Add(combined);
        }

        return new Snapshot(rows, combined, UsageMapScale.From(values, UsageMap.LevelCount));
    }

    /// <summary>
    /// Builds the row that sums the providers.
    /// </summary>
    /// <param name="reads">What each provider answered.</param>
    /// <param name="first">The map's oldest day.</param>
    /// <param name="values">The ranking every square is scaled against, added to here.</param>
    /// <returns>The combined row.</returns>
    private static UsageMapRow Combine(IReadOnlyList<Read> reads, DateOnly first, List<long> values)
    {
        var cells = new UsageMapCell[DaysShown];

        for (int offset = 0; offset < DaysShown; offset++)
        {
            DateOnly day = first.AddDays(offset);

            List<Contribution> contributions = new(reads.Count);
            TokenTotals? summed = null;
            long? total = null;

            foreach (Read read in reads)
            {
                UsageDay? stored = null;
                if (read.Days.TryGetValue(day, out UsageDay? found))
                {
                    stored = found;
                    summed = Add(summed, found.Tokens);
                    total = Add(total, found.TotalTokens);
                }

                contributions.Add(new Contribution(read.Name, stored));
            }

            // Known if any provider named a figure for it, which is what the square draws. A
            // day one of them was not installed for is still known from the other; a day
            // every row has a peak for and none has tokens for is not, because there is no
            // volume to show. The tooltip still says who had what.
            // No percentage, ever: the tooltip lists each provider's own on its own line.
            cells[offset] = new UsageMapCell(day, total, PeakPercent: null,
                                             IsKnown: total is not null,
                                             CombinedDetail(day, contributions, total, summed));

            if (total is { } value)
            {
                values.Add(value);
            }
        }

        return new UsageMapRow(CombinedName, cells, IsCombined: true);
    }

    private static Dictionary<DateOnly, UsageDay> Index(IReadOnlyList<UsageDay> days)
    {
        Dictionary<DateOnly, UsageDay> indexed = [];
        foreach (UsageDay day in days)
        {
            indexed[day.Day] = day;
        }

        return indexed;
    }

    /// <summary>Adds two figures, keeping an unreported one out of the sum entirely.</summary>
    /// <param name="running">What has been added so far, or null when nothing has.</param>
    /// <param name="next">The next figure, which may be unreported.</param>
    /// <returns>The sum, or <see langword="null"/> when neither was reported.</returns>
    private static long? Add(long? running, long? next) => (running, next) switch
    {
        (null, null) => null,
        ({ } left, null) => left,
        (null, { } right) => right,
        ({ } left, { } right) => left + right,
    };

    private static TokenTotals? Add(TokenTotals? running, TokenTotals? next)
    {
        if (next is null)
        {
            return running;
        }

        return running is null
            ? next
            : new TokenTotals(
                Add(running.Input, next.Input),
                Add(running.Output, next.Output),
                Add(running.CacheRead, next.CacheRead),
                Add(running.CacheWrite, next.CacheWrite));
    }

    /// <summary>The words for one provider's day.</summary>
    /// <param name="providerName">The provider the row belongs to.</param>
    /// <param name="stored">The stored day.</param>
    private static string ProviderDetail(string providerName, UsageDay stored)
    {
        List<string> lines =
        [
            DateLine(stored.Day),
            stored.TotalTokens is { } total
                ? string.Concat(UsageFormat.Count(total), " tokens")
                : "No token figures reported",
        ];

        if (Components(stored.Tokens) is { } components)
        {
            lines.Add(components);
        }

        lines.Add(UsageFormat.Percent(stored.PeakPercent) is { } peak
            ? string.Concat("Peak ", peak)
            : "Peak not reported");

        // Where the number came from, so a later backfill cannot quietly be read as something
        // Altim watched happen.
        lines.Add(stored.Source == UsageDaySource.Observed
            ? "Observed by Altim"
            : string.Concat("Backfilled from ", providerName));

        return string.Join('\n', lines);
    }

    /// <summary>The words for the combined row's day.</summary>
    /// <param name="day">The day.</param>
    /// <param name="contributions">What each provider had for it, in row order.</param>
    /// <param name="total">The summed token figure, or null when none was reported.</param>
    /// <param name="summed">The summed components, or null when none were reported.</param>
    private static string CombinedDetail(
        DateOnly day,
        IReadOnlyList<Contribution> contributions,
        long? total,
        TokenTotals? summed)
    {
        List<string> contributors = [];
        foreach (Contribution contribution in contributions)
        {
            if (contribution.Day is not null)
            {
                contributors.Add(contribution.Name);
            }
        }

        if (contributors.Count == 0)
        {
            return string.Join('\n', DateLine(day), "No data for this day");
        }

        // Who the figure covers, named on the figure's own line. A sum over some of the
        // providers is not a sum over all of them, and the two must not read the same.
        List<string> lines =
        [
            DateLine(day),
            total is { } value
                ? string.Concat(UsageFormat.Count(value), " tokens from ", Names(contributors))
                : string.Concat("No token figures reported from ", Names(contributors)),
        ];

        if (Components(summed) is { } components)
        {
            lines.Add(components);
        }

        foreach (Contribution contribution in contributions)
        {
            lines.Add(contribution.Day is { } stored
                ? UsageFormat.Percent(stored.PeakPercent) is { } peak
                    ? string.Concat(contribution.Name, " peak ", peak)
                    : string.Concat(contribution.Name, " peak not reported")
                : string.Concat("No data from ", contribution.Name));
        }

        return string.Join('\n', lines);
    }

    private static string UnknownDetail(DateOnly day) =>
        string.Join('\n', DateLine(day), "No data for this day");

    private static string DateLine(DateOnly day) => day.ToString("D", CultureInfo.CurrentCulture);

    /// <summary>
    /// The four components a day was reported in, or nothing when none of them were.
    /// </summary>
    /// <param name="tokens">The day's components.</param>
    /// <remarks>
    /// A component the provider did not report is left out rather than written as a zero: a
    /// day dominated by cache reads is not the same day as one dominated by output, and a zero
    /// standing in for silence would hide which of the two it was.
    /// </remarks>
    private static string? Components(TokenTotals? tokens) => tokens is null
        ? null
        : UsageFormat.Join(
            Component("Input", tokens.Input),
            Component("Output", tokens.Output),
            Component("Cache read", tokens.CacheRead),
            Component("Cache write", tokens.CacheWrite));

    private static string? Component(string label, long? value) =>
        value is { } count ? string.Concat(label, " ", UsageFormat.Count(count)) : null;

    /// <summary>Names a list of providers the way a sentence would.</summary>
    /// <param name="names">The names, in row order.</param>
    private static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => string.Concat(names[0], " and ", names[1]),
        _ => string.Concat(string.Join(", ", names.Take(names.Count - 1)), " and ", names[^1]),
    };

    /// <summary>One provider's answer.</summary>
    /// <param name="Name">The provider's display name.</param>
    /// <param name="Days">
    /// The days it had, keyed by day. Empty both when the provider had none and when its store
    /// could not be read, because a square says the same thing either way: nothing is known
    /// about that day.
    /// </param>
    private sealed record Read(string Name, Dictionary<DateOnly, UsageDay> Days);

    /// <summary>What one provider had for one day of the combined row.</summary>
    /// <param name="Name">The provider's display name.</param>
    /// <param name="Day">The stored day, or null when that provider knew nothing about it.</param>
    private sealed record Contribution(string Name, UsageDay? Day);

    /// <summary>A whole picture, built off the dispatcher and assigned in one go.</summary>
    /// <param name="Rows">Every row, the combined one last.</param>
    /// <param name="Combined">The combined row, or null when there is only one provider.</param>
    /// <param name="Scale">The ranking every square on screen is coloured against.</param>
    private sealed record Snapshot(
        IReadOnlyList<UsageMapRow> Rows,
        UsageMapRow? Combined,
        UsageMapScale Scale);
}
