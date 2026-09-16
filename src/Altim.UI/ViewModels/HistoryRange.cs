namespace Altim.UI.ViewModels;

/// <summary>
/// One of the three spans the history tape draws.
/// </summary>
/// <param name="Name">The span's name, as it reads in the picker.</param>
/// <param name="Length">How far back the span reaches from now.</param>
/// <param name="Buckets">How many points the line is drawn with.</param>
/// <param name="Ticks">How many dates are written along the bottom of the plot.</param>
/// <param name="TickFormat">The format those dates are written in.</param>
/// <remarks>
/// The bucket counts are all above the tape's dot limit, so the three spans draw as lines
/// rather than dotted lines: a 30 minute step over a day, four hours over a week and twelve
/// hours over a month. The tick counts are far lower, because an axis label per bucket is not
/// an axis, and each one is set in the unit the span is read in: a time of day over a day and
/// a date over a week or a month.
///
/// Each count divides its span into whole units - six hours, one day, five days - so a label
/// never lands halfway through a day and prints the same date as the one before it. How many
/// of them there is room to draw is the tape's business rather than this record's.
/// </remarks>
public sealed record HistoryRange(string Name, TimeSpan Length, int Buckets, int Ticks, string TickFormat)
{
    /// <summary>The last 24 hours, in half hour steps.</summary>
    public static HistoryRange Day { get; } = new("Last 24 hours", TimeSpan.FromHours(24), 48, 5, "t");

    /// <summary>The last 7 days, in four hour steps.</summary>
    public static HistoryRange Week { get; } = new("Last 7 days", TimeSpan.FromDays(7), 42, 8, "MMM d");

    /// <summary>The last 30 days, in twelve hour steps.</summary>
    public static HistoryRange Month { get; } = new("Last 30 days", TimeSpan.FromDays(30), 60, 7, "MMM d");

    /// <summary>The three spans, in the order they are offered.</summary>
    public static IReadOnlyList<HistoryRange> All { get; } = [Day, Week, Month];
}
