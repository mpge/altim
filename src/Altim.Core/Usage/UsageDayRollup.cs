using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// Collapses one provider's readings for one local calendar day into that day's row. Pure:
/// no clock, no storage, no state, and no date arithmetic of its own, so the same samples
/// always produce the same day on any machine in any timezone — and, because every figure it
/// keeps is a maximum, in any order.
/// </summary>
/// <remarks>
/// <para>
/// The rule this class exists to hold: a reading's token totals are <b>cumulative</b>, not the
/// work done since the previous reading. Summing a day's readings therefore multiplies the day
/// by however many readings it happened to take — the same work polled every minute would
/// report sixty times the figure it reports polled hourly.
/// </para>
/// <para>
/// The day's figure is the <b>highest</b> reading of the day, taken per component. Taking the
/// last reading instead is only safe for a counter that cannot fall within a day, and Altim's
/// cannot promise that: the Codex figure is summed over a bounded window of the most recent
/// rollout files, so on a busy day an older session drops out of that window and a later
/// reading legitimately reports less; the Claude figure has the same question around session
/// eviction, and around a restart mid-day rebuilding its cumulative totals from the scanner's
/// cursor. Understating a day silently is worse than the alternative, and for a counter that
/// does behave the maximum and the last reading are the same number, so the rule costs nothing
/// where it is not needed.
/// </para>
/// <para>
/// Per component rather than per reading, because a reading may report some components and not
/// others: keeping the whole row of one reading would discard a component that reading did not
/// report but another did, and would let a dip in one component drag an unrelated one down with
/// it. The consequence is that the four components may come from different readings, so the row
/// is a composite and not a snapshot of any single instant. That is still the least-wrong
/// answer, because each component is its own cumulative counter and the day's highest value for
/// it is the most of it Altim ever actually saw; the alternative is reporting less of a
/// component than was observed.
/// </para>
/// <para>
/// A percentage is the opposite kind of number: it is a level, not a counter, so the day's peak
/// is the highest any of that provider's windows reached. Percentages are never combined across
/// providers, and nothing here looks at more than one provider's readings.
/// </para>
/// </remarks>
public static class UsageDayRollup
{
    /// <summary>
    /// Rolls one provider's readings for one day up into that day's row.
    /// </summary>
    /// <param name="providerId">The provider the day belongs to.</param>
    /// <param name="day">
    /// The user's local calendar day the samples fall on. Grouping samples into days is the
    /// caller's job, because only the caller knows which local day an instant fell on; this
    /// method takes the day it is given and never re-derives it.
    /// </param>
    /// <param name="samples">
    /// That provider's samples for that day, in any order — order cannot change the result.
    /// Samples belonging to another provider or another day are not filtered out; see
    /// <paramref name="day"/>.
    /// </param>
    /// <param name="updatedAt">The stamp to put on the row.</param>
    /// <returns>
    /// The day, always <see cref="UsageDaySource.Observed"/>, each token component the highest
    /// any reading reported for it and the peak the highest percentage any reading reported. A
    /// component no reading reported stays <see langword="null"/> and is never zero, and a
    /// reading that reported nothing can never lower a component that another reading did
    /// report.
    /// <para>
    /// <see langword="null"/> when the samples report nothing at all — no tokens and no usable
    /// percentage, which includes the empty set. That day stays unknown instead of being written
    /// as an empty observed row, which would be indistinguishable from unknown on the map while
    /// also outranking any later backfill that did know what happened.
    /// </para>
    /// </returns>
    public static UsageDay? FromSamples(
        string providerId,
        DateOnly day,
        IReadOnlyList<UsageSample> samples,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(samples);

        long? input = null;
        long? output = null;
        long? cacheRead = null;
        long? cacheWrite = null;
        double? peak = null;

        for (int i = 0; i < samples.Count; i++)
        {
            UsageSample sample = samples[i];

            // A reading Altim would not show is not a reading it may take a peak from: the
            // known defect where a timestamp lands in the percentage field must leave the
            // peak unknown rather than inventing a figure out of it.
            double? reported = UsagePercent.Normalise(sample.UsedPercent);
            if (reported is { } percent && (peak is not { } highestPercent || percent > highestPercent))
            {
                peak = percent;
            }

            if (sample.Tokens is not { } tokens)
            {
                continue;
            }

            input = Higher(input, tokens.Input);
            output = Higher(output, tokens.Output);
            cacheRead = Higher(cacheRead, tokens.CacheRead);
            cacheWrite = Higher(cacheWrite, tokens.CacheWrite);
        }

        TokenTotals? totals = input is null && output is null && cacheRead is null && cacheWrite is null
            ? null
            : new TokenTotals(input, output, cacheRead, cacheWrite);

        return totals is null && peak is null
            ? null
            : new UsageDay(providerId, day, totals, peak, UsageDaySource.Observed, updatedAt);
    }

    /// <summary>
    /// The higher of what the day has so far and what a reading just reported, where an
    /// unreported figure is absent rather than zero and so can never be the higher of the two.
    /// </summary>
    /// <param name="running">The day's highest so far, or <see langword="null"/> if none yet.</param>
    /// <param name="reported">The reading's figure, or <see langword="null"/> if it reported none.</param>
    private static long? Higher(long? running, long? reported) => (running, reported) switch
    {
        (null, null) => null,
        (null, { } only) => only,
        ({ } only, null) => only,
        ({ } a, { } b) => a > b ? a : b,
    };
}
