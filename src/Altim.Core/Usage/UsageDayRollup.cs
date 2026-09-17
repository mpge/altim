using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// Collapses one provider's readings for one local calendar day into that day's row. Pure:
/// no clock, no storage, no state, and no date arithmetic of its own, so the same samples
/// always produce the same day on any machine in any timezone — and, because the one figure
/// it keeps is a maximum, in any order.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule this class exists to hold: a reading's token totals are never a per-day
/// amount, so the rollup keeps no token figure at all.</b> Not the sum of the day's
/// readings, not the last of them, and not the highest of them — none of the three is a
/// quantity the day actually spent, because no single reading was ever one either. A day's
/// tokens come only from a per-day source, which is the providers' own history through the
/// backfill; this class supplies the day's peak percentage and nothing else.
/// </para>
/// <para>
/// The reason is in what each provider publishes, and both mechanisms say the same thing
/// from different directions:
/// </para>
/// <para>
/// <b>Claude Code</b> reports a running total. The provider merges every transcript its
/// incremental scanner has ever read into one cumulative bucket and publishes that, so a
/// reading is the whole of everything scanned up to that moment, with no boundary at
/// midnight and no way to subtract the part that belongs to an earlier day.
/// </para>
/// <para>
/// <b>Codex</b> reports a sum over a bounded window: the most recently active sessions from
/// its state database, each contributing that session's own cumulative total. Which sessions
/// count as "most recent" changes between one read and the next, so the figure moves in both
/// directions for reasons that have nothing to do with usage. Measured against the live
/// store it alternated between 255,886,883 and 184,586,597 inside the same minute.
/// </para>
/// <para>
/// So the largest reading seen during a day is a running total that happened to be observed
/// that day. Summing readings multiplies a running total by however many times it was
/// polled; taking the last one reports whichever sessions were in the window at the time;
/// taking the highest one reports the high-water mark of a counter, which is not a day's
/// spend either. Writing any of them into the day's row states a figure the map then draws
/// as that day's volume, which is a claim Altim cannot make.
/// </para>
/// <para>
/// A percentage is the opposite kind of number. Each reading is a point-in-time measurement
/// against a live window, so the highest one seen during a day is a real fact about that day:
/// this is how close to the limit the user came. That is what this class keeps, normalised
/// through <see cref="UsagePercent.Normalise"/>. Percentages are never combined across
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
    /// The day, always <see cref="UsageDaySource.Observed"/>, carrying no tokens at all and
    /// a peak that is the highest percentage any reading reported. A reading that reported
    /// no usable percentage can never lower a peak another reading did report.
    /// <para>
    /// <see langword="null"/> when no reading reported a usable percentage, which includes
    /// the empty set and a day whose every reading carried tokens and nothing else. There is
    /// nothing true left to write about such a day: its readings' token figures are not its
    /// own, so a row would say only that Altim was running, and would be indistinguishable
    /// on the map from a day it knew nothing about.
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

        double? peak = null;

        for (int i = 0; i < samples.Count; i++)
        {
            // A reading Altim would not show is not a reading it may take a peak from: the
            // known defect where a timestamp lands in the percentage field must leave the
            // peak unknown rather than inventing a figure out of it.
            double? reported = UsagePercent.Normalise(samples[i].UsedPercent);
            if (reported is { } percent && (peak is not { } highest || percent > highest))
            {
                peak = percent;
            }
        }

        // Tokens are deliberately null and are not read from the samples at all. See the
        // class remarks: there is no arithmetic over running totals that yields a day.
        return peak is null
            ? null
            : new UsageDay(providerId, day, Tokens: null, peak, UsageDaySource.Observed, updatedAt);
    }
}
