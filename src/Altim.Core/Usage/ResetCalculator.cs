using System.Globalization;
using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// Arithmetic around a window reset instant. Every reading of "now" comes from the
/// injected <see cref="TimeProvider"/>, so behaviour across a reset boundary is testable
/// without waiting for one.
/// </summary>
public sealed class ResetCalculator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a calculator that reads the current instant from <paramref name="timeProvider"/>.
    /// </summary>
    /// <param name="timeProvider">The clock. Never <see langword="null"/>.</param>
    public ResetCalculator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>The current instant in UTC, as the injected clock reports it.</summary>
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    /// <summary>
    /// How long until <paramref name="instant"/>.
    /// </summary>
    /// <param name="instant">The instant to measure to.</param>
    /// <returns>
    /// The remaining time, or <see cref="TimeSpan.Zero"/> once the instant has passed.
    /// Never negative: a negative remaining time is not something the UI should render.
    /// </returns>
    public TimeSpan TimeUntil(DateTimeOffset instant)
    {
        DateTimeOffset now = UtcNow;
        return instant > now ? instant - now : TimeSpan.Zero;
    }

    /// <summary>
    /// True when <paramref name="instant"/> is at or before now.
    /// </summary>
    /// <param name="instant">The instant to test.</param>
    public bool HasPassed(DateTimeOffset instant) => instant <= UtcNow;

    /// <summary>
    /// How long until the window rolls over.
    /// </summary>
    /// <param name="window">The window, or <see langword="null"/> when none was reported.</param>
    /// <returns>
    /// <see langword="null"/> when there is no window, or when the provider reported no
    /// reset instant; an unknown reset instant is never computed from the window length.
    /// <see cref="TimeSpan.Zero"/> once the reset instant has passed.
    /// </returns>
    public TimeSpan? TimeUntilReset(LimitWindow? window) =>
        window?.ResetsAt is { } resetsAt ? TimeUntil(resetsAt) : null;

    /// <summary>
    /// True when the reported reset instant of the window has passed.
    /// </summary>
    /// <param name="window">The window, or <see langword="null"/> when none was reported.</param>
    /// <returns>
    /// False when there is no window or no reported reset instant: an unknown reset
    /// instant is never treated as a reset that happened.
    /// </returns>
    public bool HasReset(LimitWindow? window) =>
        window?.ResetsAt is { } resetsAt && HasPassed(resetsAt);

    /// <summary>
    /// The humanised time remaining on a window, for example "2h 14m".
    /// </summary>
    /// <param name="window">The window, or <see langword="null"/> when none was reported.</param>
    /// <returns>
    /// <see langword="null"/> when no reset instant is known, so the caller can say the
    /// reset time is unknown rather than show "under a minute".
    /// </returns>
    public string? DescribeTimeUntilReset(LimitWindow? window) =>
        TimeUntilReset(window) is { } remaining ? Humanise(remaining) : null;

    /// <summary>
    /// Formats a duration the way the UI reads it: "3d 4h", "2h 14m", "14m" or
    /// "under a minute". Components are truncated rather than rounded up. The logic is
    /// locale independent by construction: every number is formatted with the invariant
    /// culture and the caller localises the text if it ever needs to.
    /// </summary>
    /// <param name="remaining">
    /// The duration. Zero and negative durations both read as "under a minute"; a caller
    /// that needs to distinguish "already reset" uses <see cref="HasReset(LimitWindow?)"/>.
    /// </param>
    public static string Humanise(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        if (remaining.TotalMinutes < 1d)
        {
            return "under a minute";
        }

        if (remaining.TotalDays >= 1d)
        {
            int days = remaining.Days;
            int trailingHours = remaining.Hours;
            return trailingHours > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{days}d {trailingHours}h")
                : string.Create(CultureInfo.InvariantCulture, $"{days}d");
        }

        int hours = remaining.Hours;
        int minutes = remaining.Minutes;
        if (hours > 0)
        {
            return minutes > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{hours}h {minutes}m")
                : string.Create(CultureInfo.InvariantCulture, $"{hours}h");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{minutes}m");
    }
}
