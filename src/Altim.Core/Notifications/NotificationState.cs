namespace Altim.Core.Notifications;

/// <summary>
/// The record that one notification has already fired, so it cannot fire twice for the
/// same window. This is the shape of a row in the <c>notification_state</c> table, whose
/// primary key is the same three values.
/// </summary>
/// <param name="ProviderId">The provider the notification was about.</param>
/// <param name="MetricKey">The metric the notification was about.</param>
/// <param name="Threshold">The threshold percentage that fired.</param>
/// <param name="FiredAt">When it fired.</param>
/// <param name="WindowResetsAt">
/// The reset instant of the window it fired in. <see langword="null"/> when the provider
/// reported no reset instant, in which case the entry is never cleared by time passing,
/// only by the threshold itself changing: with no reset instant there is no way to know a
/// new window has begun, and firing again on a guess would be worse than staying quiet.
/// </param>
public sealed record NotificationState(
    string ProviderId,
    string MetricKey,
    int Threshold,
    DateTimeOffset FiredAt,
    DateTimeOffset? WindowResetsAt);
