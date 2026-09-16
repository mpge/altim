namespace Altim.UI.ViewModels;

/// <summary>
/// One of the three spans the history page draws.
/// </summary>
/// <param name="Name">The span's name, as it reads in the picker.</param>
/// <param name="Length">How far back the span reaches from now.</param>
/// <param name="Buckets">How many points the line is drawn with.</param>
/// <remarks>
/// The bucket counts are all above the tape's 32 point dot limit, so the three spans draw as
/// lines rather than dotted lines: a 30 minute step over a day, four hours over a week and
/// twelve hours over a month.
/// </remarks>
public sealed record HistoryRange(string Name, TimeSpan Length, int Buckets)
{
    /// <summary>The last 24 hours, in half hour steps.</summary>
    public static HistoryRange Day { get; } = new("24 hours", TimeSpan.FromHours(24), 48);

    /// <summary>The last 7 days, in four hour steps.</summary>
    public static HistoryRange Week { get; } = new("7 days", TimeSpan.FromDays(7), 42);

    /// <summary>The last 30 days, in twelve hour steps.</summary>
    public static HistoryRange Month { get; } = new("30 days", TimeSpan.FromDays(30), 60);

    /// <summary>The three spans, in the order they are offered.</summary>
    public static IReadOnlyList<HistoryRange> All { get; } = [Day, Week, Month];
}
