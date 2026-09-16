namespace Altim.UI.Formatting;

/// <summary>
/// The Overview greeting. Time based and plain, exactly the three forms DESIGN.md allows.
/// </summary>
/// <remarks>
/// DESIGN.md fixes the words but not the cutovers, so the boundaries are stated here once and
/// tested: morning from 05:00, afternoon from 12:00, evening from 18:00 and through the small
/// hours. The hour always comes from the machine's local time, never a stored or assumed value.
/// </remarks>
public static class Greeting
{
    /// <summary>The first hour of the day that reads as morning.</summary>
    public const int MorningStartHour = 5;

    /// <summary>The first hour of the day that reads as afternoon.</summary>
    public const int AfternoonStartHour = 12;

    /// <summary>The first hour of the day that reads as evening.</summary>
    public const int EveningStartHour = 18;

    /// <summary>The greeting for the hours between <see cref="MorningStartHour"/> and noon.</summary>
    public const string Morning = "Good morning";

    /// <summary>The greeting for the hours between noon and <see cref="EveningStartHour"/>.</summary>
    public const string Afternoon = "Good afternoon";

    /// <summary>The greeting for the evening and the small hours.</summary>
    public const string Evening = "Good evening";

    /// <summary>The line that follows the greeting on Overview.</summary>
    public const string SubHeading = "Here's how your AI agents are doing.";

    /// <summary>Picks the greeting for an hour of the day.</summary>
    /// <param name="hour">The local hour, 0 through 23.</param>
    /// <returns><see cref="Morning"/>, <see cref="Afternoon"/> or <see cref="Evening"/>.</returns>
    public static string ForHour(int hour)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hour);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour, 23);

        if (hour < MorningStartHour || hour >= EveningStartHour)
        {
            return Evening;
        }

        return hour < AfternoonStartHour ? Morning : Afternoon;
    }

    /// <summary>Picks the greeting for an instant, read in its own offset.</summary>
    /// <param name="localNow">The local time to read the hour from.</param>
    public static string For(DateTimeOffset localNow) => ForHour(localNow.Hour);

    /// <summary>Picks the greeting for the current local time.</summary>
    /// <param name="timeProvider">The clock to read. Tests substitute their own.</param>
    public static string Now(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return For(timeProvider.GetLocalNow());
    }
}
