namespace Altim.Core.Notifications;

/// <summary>
/// Everything <see cref="ThresholdEvaluator"/> remembers between evaluations, as one
/// immutable value. Passing it in and getting a new one back is what keeps evaluation a
/// pure function and the whole notification path testable without a desktop.
/// </summary>
/// <param name="EvaluatedProviders">
/// The provider ids that have already been through an evaluation. Start-up silence is
/// per provider, not global: readings arrive one provider at a time, so a second
/// provider reporting for the first time three ticks in is still having its first
/// reading and must not replay a threshold it was already over as an alert.
/// </param>
/// <param name="Fired">
/// One entry per notification that has fired and not yet been cleared by its window
/// rolling over. Empty means nothing has fired, which is also the correct starting point
/// for a fresh install.
/// </param>
public sealed record ThresholdState(
    IReadOnlyList<string> EvaluatedProviders,
    IReadOnlyList<NotificationState> Fired)
{
    /// <summary>
    /// The state to start from: nothing has fired, no provider has been evaluated, and
    /// every provider's first evaluation is therefore silent.
    /// </summary>
    public static ThresholdState Initial { get; } = new([], []);

    /// <summary>
    /// True once any provider has been evaluated. Kept for callers that only need to
    /// know whether this process has evaluated anything at all; the per provider answer
    /// is <see cref="HasEvaluatedProvider(string)"/>.
    /// </summary>
    public bool HasEvaluated => EvaluatedProviders.Count > 0;

    /// <summary>
    /// Rebuilds the state from rows loaded out of <c>notification_state</c> after a
    /// restart.
    /// </summary>
    /// <param name="fired">The persisted entries. Never <see langword="null"/>.</param>
    /// <returns>
    /// A state that still suppresses the first evaluation of every provider, because a
    /// restart is a start: what was on screen before the process died is not worth
    /// re-announcing.
    /// </returns>
    public static ThresholdState FromPersisted(IReadOnlyList<NotificationState> fired)
    {
        ArgumentNullException.ThrowIfNull(fired);
        return new ThresholdState([], fired);
    }

    /// <summary>
    /// True when <paramref name="providerId"/> has already been through an evaluation,
    /// which is what makes it eligible to raise a notification.
    /// </summary>
    /// <param name="providerId">The provider to test. Never <see langword="null"/>.</param>
    public bool HasEvaluatedProvider(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);

        foreach (string evaluated in EvaluatedProviders)
        {
            if (string.Equals(evaluated, providerId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
