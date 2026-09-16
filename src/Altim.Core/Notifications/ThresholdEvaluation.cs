using Altim.Core.Models;

namespace Altim.Core.Notifications;

/// <summary>
/// The outcome of one evaluation: what to show, and what to remember.
/// </summary>
/// <param name="Notifications">
/// The notifications to hand to the platform, in the order they were produced. Empty is
/// the normal case, and is what the first evaluation after start always produces.
/// </param>
/// <param name="State">
/// The state to keep for the next evaluation and to persist. Always returned, including
/// when no notification fired, because an evaluation that fires nothing still records
/// thresholds it found already crossed and drops entries whose window has rolled over.
/// </param>
public sealed record ThresholdEvaluation(
    IReadOnlyList<Notification> Notifications,
    ThresholdState State);
