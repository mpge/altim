using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// Collapses one provider's readings for one local calendar day into that day's row. Pure:
/// no clock, no storage, no state, and no date arithmetic of its own, so the same samples
/// always produce the same day on any machine in any timezone.
/// </summary>
/// <remarks>
/// <para>
/// The rule this class exists to hold: a reading's token totals are <b>cumulative</b>, not
/// the work done since the previous reading. Summing a day's readings therefore multiplies
/// the day by however many readings it happened to take — the same work polled every minute
/// would report sixty times the figure it reports polled hourly. The day's tokens are
/// the last reading of that day that reported any, taken whole rather than stitched together
/// per component, which is the rule <see cref="Altim.Core.Models.UsageSample"/> rows are
/// already collapsed by when the retention pass down-samples them.
/// </para>
/// <para>
/// A percentage is the opposite kind of number: it is a level, not a counter, so the day's
/// peak is the highest any of that provider's windows reached. Percentages are never
/// combined across providers, and nothing here looks at more than one provider's readings.
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
    /// That provider's samples for that day, in any order. Samples belonging to another
    /// provider or another day are not filtered out — see <paramref name="day"/>.
    /// </param>
    /// <param name="updatedAt">The stamp to put on the row.</param>
    /// <returns>
    /// The day, always <see cref="UsageDaySource.Observed"/>, with its tokens taken from the
    /// last reading that reported any and its peak the highest percentage any reading
    /// reported. A component no reading reported stays <see langword="null"/> and is never
    /// zero.
    /// <para>
    /// <see langword="null"/> when the samples report nothing at all — no tokens and no
    /// usable percentage, which includes the empty set. That day stays unknown instead of
    /// being written as an empty observed row, which would be indistinguishable from unknown
    /// on the map while also outranking any later backfill that did know what happened.
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

        TokenTotals? tokens = null;
        DateTimeOffset tokensCapturedAt = default;
        double? peak = null;

        for (int i = 0; i < samples.Count; i++)
        {
            UsageSample sample = samples[i];

            // A reading Altim would not show is not a reading it may take a peak from: the
            // known defect where a timestamp lands in the percentage field must leave the
            // peak unknown rather than inventing a figure out of it.
            if (UsagePercent.Normalise(sample.UsedPercent) is { } percent &&
                (peak is not { } highest || percent > highest))
            {
                peak = percent;
            }

            // Later wins, and at the same instant the later sample in the given order wins,
            // so the result depends on the samples and on nothing else.
            if (ReportedTokens(sample.Tokens) &&
                (tokens is null || sample.CapturedAt >= tokensCapturedAt))
            {
                tokens = sample.Tokens;
                tokensCapturedAt = sample.CapturedAt;
            }
        }

        return tokens is null && peak is null
            ? null
            : new UsageDay(providerId, day, tokens, peak, UsageDaySource.Observed, updatedAt);
    }

    /// <summary>
    /// Whether a reading carried any token figure at all. Totals whose every component is
    /// <see langword="null"/> reported nothing, so they must not displace an earlier reading
    /// that did.
    /// </summary>
    /// <param name="tokens">The reading's totals, or <see langword="null"/> when it had none.</param>
    private static bool ReportedTokens(TokenTotals? tokens) =>
        tokens is not null &&
        (tokens.Input is not null || tokens.Output is not null ||
         tokens.CacheRead is not null || tokens.CacheWrite is not null);
}
