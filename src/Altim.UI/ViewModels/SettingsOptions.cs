using System.Globalization;
using Altim.Core.Settings;

namespace Altim.UI.ViewModels;

/// <summary>
/// One entry in the theme picker.
/// </summary>
/// <param name="Value">The stored preference.</param>
/// <param name="Name">The name shown in the picker.</param>
public sealed record ThemeOption(ThemePreference Value, string Name)
{
    /// <summary>The three preferences, in the order they are offered.</summary>
    public static IReadOnlyList<ThemeOption> All { get; } =
    [
        new(ThemePreference.System, "System"),
        new(ThemePreference.Light, "Light"),
        new(ThemePreference.Dark, "Dark"),
    ];

    /// <summary>Finds the entry for a stored preference.</summary>
    /// <param name="preference">The stored preference.</param>
    public static ThemeOption For(ThemePreference preference)
    {
        foreach (ThemeOption option in All)
        {
            if (option.Value == preference)
            {
                return option;
            }
        }

        return All[0];
    }
}

/// <summary>
/// One entry in the refresh interval picker.
/// </summary>
/// <param name="Value">The interval between polls.</param>
/// <param name="Name">The name shown in the picker.</param>
public sealed record RefreshOption(TimeSpan Value, string Name)
{
    /// <summary>The intervals offered by default.</summary>
    public static IReadOnlyList<RefreshOption> Standard { get; } =
    [
        new(TimeSpan.FromSeconds(30), "30 seconds"),
        new(TimeSpan.FromMinutes(1), "1 minute"),
        new(TimeSpan.FromMinutes(2), "2 minutes"),
        new(TimeSpan.FromMinutes(5), "5 minutes"),
    ];

    /// <summary>
    /// Describes an interval that is not one of the standard entries, so a value written by
    /// hand still shows as itself rather than being silently rounded to a neighbour.
    /// </summary>
    /// <param name="interval">The stored interval.</param>
    public static RefreshOption Custom(TimeSpan interval)
    {
        double seconds = Math.Max(1d, Math.Round(interval.TotalSeconds));
        string name = seconds < 60d
            ? Plural(seconds, "second")
            : Plural(Math.Round(seconds / 60d), "minute");

        return new RefreshOption(interval, name);
    }

    private static string Plural(double count, string unit)
    {
        string number = count.ToString("0", CultureInfo.CurrentCulture);
        return count == 1d ? string.Concat(number, " ", unit) : string.Concat(number, " ", unit, "s");
    }
}

/// <summary>
/// One entry in a threshold picker.
/// </summary>
/// <param name="Value">The stored percentage.</param>
/// <param name="Name">The percentage as it reads in the picker.</param>
public sealed record ThresholdOption(int Value, string Name)
{
    /// <summary>The percentages offered by default.</summary>
    public static IReadOnlyList<int> Standard { get; } = [50, 60, 70, 75, 80, 85, 90, 95];

    /// <summary>Builds an entry for a percentage, including one that is not standard.</summary>
    /// <param name="percent">The stored percentage.</param>
    public static ThresholdOption For(int percent) =>
        new(percent, string.Concat(percent.ToString("0", CultureInfo.CurrentCulture), "%"));
}
