namespace Altim.Core.Notifications;

/// <summary>
/// Everything <see cref="ThresholdEvaluator"/> remembers between evaluations, as one
/// immutable value. Passing it in and getting a new one back is what keeps evaluation a
/// pure function and the whole notification path testable without a desktop.
/// </summary>
/// <param name="HasEvaluated">
/// False until the first evaluation after start has run. While it is false no
/// notification is produced at all, which is what stops a launch from replaying every
/// threshold already crossed as an alert storm.
/// </param>
/// <param name="Fired">
/// One entry per notification that has fired and not yet been cleared by its window
/// rolling over. Empty means nothing has fired, which is also the correct starting point
/// for a fresh install.
/// </param>
public sealed record ThresholdState(bool HasEvaluated, IReadOnlyList<NotificationState> Fired)
{
    /// <summary>
    /// The state to start from: nothing has fired, and the next evaluation is the first
    /// one and therefore silent.
    /// </summary>
    public static ThresholdState Initial { get; } = new(false, []);

    /// <summary>
    /// Rebuilds the state from rows loaded out of <c>notification_state</c> after a
    /// restart.
    /// </summary>
    /// <param name="fired">The persisted entries. Never <see langword="null"/>.</param>
    /// <returns>
    /// A state that still suppresses its first evaluation, because a restart is a start:
    /// what was on screen before the process died is not worth re-announcing.
    /// </returns>
    public static ThresholdState FromPersisted(IReadOnlyList<NotificationState> fired)
    {
        ArgumentNullException.ThrowIfNull(fired);
        return new ThresholdState(false, fired);
    }
}
