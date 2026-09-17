using System.Globalization;

namespace Altim.Core.Monitoring;

/// <summary>
/// When the maintenance pass rolls days up, when it asks a provider for its own history,
/// and how far back each of them reaches. Pure arithmetic over a clock and a calendar.
/// </summary>
/// <remarks>
/// <para>
/// None of this needs a provider, a database or a running application, which is the point.
/// The cases that decide whether the map is honest — a source that is not installed, a
/// machine resumed from sleep after a week, a clock that moved backwards — are the ones
/// hardest to stage against the real thing, so the decisions live here as functions and the
/// composition root only carries them out.
/// </para>
/// <para>
/// Neither the rollup nor the backfill has a timer. Both are folded into the housekeeping
/// pass that already runs, because the application has a measured idle cost and a second
/// wake source would be a regression against it.
/// </para>
/// </remarks>
public static class HistoryBackfill
{
    /// <summary>
    /// The shortest gap between two backfills of the same provider.
    /// </summary>
    /// <remarks>
    /// A day's granularity is a day's news. Both sources cost something real — one is a
    /// network call, the other a walk over a transcript store measured in tens of gigabytes
    /// — and neither tells us anything new inside a day that Altim's own samples do not
    /// already know better.
    /// </remarks>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// How far back a provider is asked to reach, in days. The map draws a year, so asking
    /// for more would be work nothing renders.
    /// </summary>
    public const int ReachDays = 365;

    /// <summary>
    /// The furthest back a rollup reads, in days.
    /// </summary>
    /// <remarks>
    /// A rollup reads every sample in its range, so the catch-up after a long gap needs a
    /// floor. This one sits just past the 30-day full-resolution retention window: beyond
    /// it the samples have been collapsed to one row an hour anyway, and both providers'
    /// own history covers that ground as backfill.
    /// </remarks>
    public const int MaximumRollUpDays = 35;

    /// <summary>The prefix every provider's last-backfill stamp is stored under.</summary>
    /// <remarks>
    /// One key per provider rather than one for all of them: a provider whose source is
    /// unavailable must not hold back one whose source answers, and a provider added later
    /// must start with no stamp of its own rather than inherit another's.
    /// </remarks>
    public const string LastRunKeyPrefix = "maintenance.last_backfill.";

    /// <summary>
    /// Decides whether a provider's backfill is due.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="lastRun">
    /// When this provider was last asked, or <see langword="null"/> when it never has been —
    /// which is also what an absent or unreadable stamp means.
    /// </param>
    /// <param name="sourceAvailable">
    /// Whether the provider can reach into the past at all. A provider that is not an
    /// <c>IUsageHistorySource</c> is never due: the days it cannot account for stay unknown
    /// rather than becoming a row of zeroes.
    /// </param>
    /// <returns><see langword="true"/> when the caller should ask the provider now.</returns>
    /// <remarks>
    /// A stamp ahead of <paramref name="now"/> means the clock moved backwards — a laptop
    /// that corrected itself, a restored image, a deliberate change. Treating that as "ran
    /// very recently" would hold the backfill off until real time caught up, which for a
    /// clock a fortnight out is a fortnight of unknown squares with no way to explain them.
    /// It runs instead, and the caller re-stamps it at <paramref name="now"/>, so the file
    /// heals on the first pass rather than never.
    /// </remarks>
    public static bool ShouldRun(DateTimeOffset now, DateTimeOffset? lastRun, bool sourceAvailable)
    {
        if (!sourceAvailable)
        {
            return false;
        }

        if (lastRun is not { } last)
        {
            return true;
        }

        return last > now || now - last >= MinimumInterval;
    }

    /// <summary>
    /// The <c>setting</c> key holding one provider's last backfill instant.
    /// </summary>
    /// <param name="providerId">The provider, for example <c>claude</c>.</param>
    /// <returns>The key to read and write.</returns>
    /// <remarks>
    /// The provider id is a constant of Altim's, not anything read off the machine, so the
    /// key carries no account name, path or project. It is the same identifier the sample
    /// and day tables are already keyed by.
    /// </remarks>
    public static string LastRunKey(string providerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        return LastRunKeyPrefix + providerId;
    }

    /// <summary>Formats a last-run instant for the <c>setting</c> table.</summary>
    /// <param name="at">The instant to store.</param>
    /// <returns>Unix seconds, in digits.</returns>
    /// <remarks>
    /// Invariant digits, like every other stamp in that table. A value formatted under the
    /// user's culture would read back as nothing on the next start, and the backfill would
    /// then believe it had never run on every pass for the rest of time.
    /// </remarks>
    public static string FormatLastRun(DateTimeOffset at)
        => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a last-run instant back out of the <c>setting</c> table.</summary>
    /// <param name="stored">The stored text, or <see langword="null"/> when the key is absent.</param>
    /// <returns>
    /// The instant, or <see langword="null"/> when there is nothing usable there. Missing,
    /// empty and edited-into-nonsense all mean the same thing: not that the backfill is
    /// overdue by an unknown amount, simply that nothing is known about when it last ran,
    /// which is how a fresh install starts.
    /// </returns>
    public static DateTimeOffset? ParseLastRun(string? stored)
        => long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>The oldest day a provider is asked to account for.</summary>
    /// <param name="today">The user's local calendar day.</param>
    /// <returns>The first day of the window, inclusive.</returns>
    public static DateOnly EarliestDay(DateOnly today) => today.AddDays(-ReachDays);

    /// <summary>
    /// The first local day a rollup pass should cover. The last is always today.
    /// </summary>
    /// <param name="today">The user's local calendar day.</param>
    /// <param name="lastRolledUp">
    /// The day the previous pass of this process treated as today, or
    /// <see langword="null"/> when this is the first pass since the process started.
    /// </param>
    /// <returns>The first day of the range, inclusive, never later than yesterday.</returns>
    /// <remarks>
    /// <para>
    /// <b>Yesterday and today, every pass, all day.</b> A machine left running across
    /// midnight would otherwise keep whatever partial figure the 23:55 pass happened to
    /// see; including yesterday gives that day its final rollup within one interval of
    /// midnight. Rolling a day up again is idempotent and can only raise a figure, so
    /// re-reading yesterday all day costs a small read and changes nothing.
    /// </para>
    /// <para>
    /// <b>A machine resumed from sleep is the case yesterday-and-today does not cover.</b>
    /// A laptop asleep from Saturday lunchtime to Wednesday morning wakes with yesterday
    /// being Tuesday, and Saturday — a day that really was half lived and half sampled —
    /// would stay frozen at its lunchtime figure for good. Reaching back to the last day
    /// this process rolled up finishes it instead.
    /// </para>
    /// <para>
    /// <b>The first pass of a process reaches back to the bound.</b> Nothing has been
    /// rolled up yet by this process, the samples on disk may predate the build that grew
    /// a day table at all, and an install upgraded into this feature would otherwise show
    /// unknown squares over history Altim watched for itself. It is one wide read at
    /// start-up, on a worker, bounded by <see cref="MaximumRollUpDays"/>.
    /// </para>
    /// <para>
    /// A <paramref name="lastRolledUp"/> in the future — the clock moved backwards between
    /// two passes — is ignored rather than becoming the start of an empty range that would
    /// stop rolling today up at all.
    /// </para>
    /// </remarks>
    public static DateOnly RollUpFrom(DateOnly today, DateOnly? lastRolledUp)
    {
        DateOnly floor = today.AddDays(-MaximumRollUpDays);
        DateOnly yesterday = today.AddDays(-1);

        if (lastRolledUp is not { } last || last > yesterday)
        {
            return lastRolledUp is null ? floor : yesterday;
        }

        return last < floor ? floor : last;
    }
}
