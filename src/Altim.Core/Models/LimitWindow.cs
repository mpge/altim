namespace Altim.Core.Models;

/// <summary>
/// The rolling period a usage limit is measured over.
/// </summary>
/// <param name="Length">
/// How long the window runs for. Windows are classified by this length in minutes,
/// never by a provider's slot name, and tolerate off-by-one values.
/// </param>
/// <param name="ResetsAt">
/// The instant the window rolls over. <see langword="null"/> means the provider did
/// not report a reset instant. It is never computed from <paramref name="Length"/>,
/// and the UI says the reset time is unknown rather than inventing one.
/// </param>
public sealed record LimitWindow(TimeSpan Length, DateTimeOffset? ResetsAt);
