namespace Altim.Core.Models;

/// <summary>
/// A message for the operating system notification centre. Built in
/// <c>Altim.Core</c> by a pure function over usage, settings and notification state,
/// which is what makes threshold behaviour testable without a desktop.
/// </summary>
/// <param name="Title">
/// One short sentence, sentence case, no exclamation mark, for example
/// "Session usage reached 80%".
/// </param>
/// <param name="Body">
/// Supporting detail. Empty when the title says everything there is to say.
/// </param>
/// <param name="ProviderId">
/// The provider the message is about. <see langword="null"/> means the message is not
/// about a single provider, for example an application level warning.
/// </param>
/// <param name="Tag">
/// Optional identity the platform uses to replace an earlier notification instead of
/// stacking a new one. <see langword="null"/> means the platform should show this
/// message in its own right.
/// </param>
public sealed record Notification(
    string Title,
    string Body,
    string? ProviderId,
    string? Tag);
