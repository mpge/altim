using System.Globalization;
using Altim.Core.Models;

namespace Altim.Core.Usage;

/// <summary>
/// Classifies a limit window by its length and produces the label the UI shows.
/// Providers disagree about slot names and have swapped slot positions between
/// releases, so length is the only thing Altim trusts, and it is matched tolerantly:
/// 299 and 10079 both occur in the wild.
/// </summary>
public static class LimitWindowClassifier
{
    /// <summary>Nominal length of a five hour window, in minutes.</summary>
    public const double FiveHourMinutes = 300d;

    /// <summary>Nominal length of a daily window, in minutes.</summary>
    public const double DailyMinutes = 1_440d;

    /// <summary>Nominal length of a weekly window, in minutes.</summary>
    public const double WeeklyMinutes = 10_080d;

    /// <summary>Nominal length of a monthly window, in minutes, taken as 30 days.</summary>
    public const double MonthlyMinutes = 43_200d;

    /// <summary>
    /// How far a reported length may sit from a nominal one and still match: the greater
    /// of five minutes and one percent of the nominal length.
    /// </summary>
    /// <param name="nominalMinutes">The nominal length being matched against.</param>
    public static double ToleranceMinutes(double nominalMinutes) =>
        Math.Max(5d, Math.Abs(nominalMinutes) * 0.01d);

    /// <summary>
    /// Classifies a window length given in minutes.
    /// </summary>
    /// <param name="minutes">
    /// The length the provider reported. A non-finite or non-positive value classifies as
    /// <see cref="LimitWindowKind.Other"/> rather than throwing.
    /// </param>
    public static LimitWindowKind Classify(double minutes)
    {
        if (double.IsNaN(minutes) || double.IsInfinity(minutes) || minutes <= 0d)
        {
            return LimitWindowKind.Other;
        }

        return minutes switch
        {
            _ when Matches(minutes, FiveHourMinutes) => LimitWindowKind.FiveHour,
            _ when Matches(minutes, DailyMinutes) => LimitWindowKind.Daily,
            _ when Matches(minutes, WeeklyMinutes) => LimitWindowKind.Weekly,
            _ when Matches(minutes, MonthlyMinutes) => LimitWindowKind.Monthly,
            _ => LimitWindowKind.Other,
        };
    }

    /// <summary>
    /// Classifies a window length.
    /// </summary>
    /// <param name="length">The length the provider reported.</param>
    public static LimitWindowKind Classify(TimeSpan length) => Classify(length.TotalMinutes);

    /// <summary>
    /// Classifies a reported window.
    /// </summary>
    /// <param name="window">The window, or <see langword="null"/> when none was reported.</param>
    /// <returns>
    /// <see langword="null"/> when <paramref name="window"/> is <see langword="null"/>,
    /// because "no window" is not the same as "a window of an unknown family".
    /// </returns>
    public static LimitWindowKind? Classify(LimitWindow? window) =>
        window is null ? null : Classify(window.Length);

    /// <summary>
    /// The fixed display label for a classified family.
    /// </summary>
    /// <param name="kind">The family.</param>
    /// <returns>
    /// The label, or <see langword="null"/> for <see cref="LimitWindowKind.Other"/>, which
    /// has no fixed label. Use <see cref="Label(TimeSpan)"/> to label one from its length.
    /// </returns>
    public static string? Label(LimitWindowKind kind) => kind switch
    {
        LimitWindowKind.FiveHour => "Session",
        LimitWindowKind.Daily => "Daily",
        LimitWindowKind.Weekly => "Weekly",
        LimitWindowKind.Monthly => "Monthly",
        _ => null,
    };

    /// <summary>
    /// The display label for a window length: the family label where the length matches a
    /// known family, and a plain description of the length otherwise ("3 hour", "45
    /// minute"). Never derived from a provider slot name.
    /// </summary>
    /// <param name="length">The length the provider reported.</param>
    public static string Label(TimeSpan length) => Label(Classify(length)) ?? Describe(length);

    /// <summary>
    /// The display label for a reported window.
    /// </summary>
    /// <param name="window">The window, or <see langword="null"/> when none was reported.</param>
    /// <returns><see langword="null"/> when there is no window to label.</returns>
    public static string? Label(LimitWindow? window) => window is null ? null : Label(window.Length);

    private static bool Matches(double minutes, double nominalMinutes) =>
        Math.Abs(minutes - nominalMinutes) <= ToleranceMinutes(nominalMinutes);

    private static string Describe(TimeSpan length)
    {
        if (length <= TimeSpan.Zero)
        {
            return "Other";
        }

        long minutes = (long)Math.Round(length.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes} minute");
        }

        if (minutes % 1_440 == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes / 1_440} day");
        }

        return minutes % 60 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} hour")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes} minute");
    }
}
