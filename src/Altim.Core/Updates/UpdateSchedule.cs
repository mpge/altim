namespace Altim.Core.Updates;

/// <summary>
/// When an automatic update check is due. Pure, so the one rule that decides how often
/// Altim reaches the internet can be read and tested without a network stack.
/// </summary>
/// <remarks>
/// <para>
/// Altim is a local-first utility and this is its only outbound request, so the cadence is
/// a stated number rather than a timer buried in a service: <see cref="Interval"/> once,
/// after <see cref="Settle"/> has passed since start-up.
/// </para>
/// <para>
/// The settle delay is not politeness. The cold-start budget is 800ms to a tray icon, and
/// a DNS lookup on a machine waking to a captive portal takes much longer than that; a
/// check on the start-up path would be a network request standing between the user and
/// their tray icon.
/// </para>
/// <para>
/// A failed check stamps the clock exactly as a successful one does. A laptop that is
/// offline all day would otherwise retry on every tick, which is both a worse experience
/// and a more conspicuous one on somebody else's network.
/// </para>
/// </remarks>
public static class UpdateSchedule
{
    /// <summary>How long after start-up the first automatic check may run.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);

    /// <summary>How long between automatic checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether an automatic check should run now.
    /// </summary>
    /// <param name="enabled">The user's setting. False is the whole answer.</param>
    /// <param name="startedAt">When this process started.</param>
    /// <param name="lastCheckedAt">
    /// When a check last ran in this process, or null when none has. Deliberately
    /// per-process rather than persisted: the stored value would have to survive a
    /// downgrade, a clock change and a database that would not open, and the cost of
    /// getting it wrong is either a check that never runs again or one that runs on every
    /// start.
    /// </param>
    /// <param name="now">The current instant.</param>
    /// <returns>True when a check is due.</returns>
    public static bool IsDue(
        bool enabled,
        DateTimeOffset startedAt,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now)
    {
        if (!enabled)
        {
            return false;
        }

        if (now - startedAt < Settle)
        {
            return false;
        }

        if (lastCheckedAt is null)
        {
            return true;
        }

        // A clock that moved backwards — a resumed laptop correcting itself, a machine
        // that has just synchronised — leaves a stamp in the future. Treating that as
        // "not due" would suspend checks until the clock caught up, so any negative
        // elapsed time counts as due.
        TimeSpan since = now - lastCheckedAt.Value;
        return since < TimeSpan.Zero || since >= Interval;
    }
}
