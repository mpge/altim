using Altim.Core.Models;

namespace Altim.Core.Abstractions;

/// <summary>
/// Delivers a message to the operating system notification centre.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Shows one notification. Returns once the platform has accepted the message, not
    /// when the user reads or dismisses it. A platform that refuses notifications, or
    /// has them switched off, completes without throwing: a missing notification is
    /// never worth an error dialog.
    /// </summary>
    /// <param name="n">The message to show.</param>
    /// <param name="ct">Cancels the call before the message is handed to the platform.</param>
    ValueTask ShowAsync(Notification n, CancellationToken ct);
}
