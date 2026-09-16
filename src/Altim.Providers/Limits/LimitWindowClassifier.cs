using System.Globalization;

namespace Altim.Providers.Limits;

/// <summary>
/// Turns a window length in minutes into a stable key fragment and a display label.
/// </summary>
/// <remarks>
/// <para>
/// Providers report lengths that drift by a minute or two — 299 and 10,079 both occur in
/// the field — so matching is tolerant. Tolerance is the larger of five minutes and one
/// per cent of the nominal length, which is wide enough for the observed drift and far
/// too narrow for two nominal lengths to collide.
/// </para>
/// <para>
/// The normalised length matters beyond cosmetics. Metric keys are storage keys, so a
/// window reported as 10,079 minutes on one poll and 10,080 on the next has to produce the
/// same key, or the history for that limit silently splits into two series.
/// </para>
/// </remarks>
public static class LimitWindowClassifier
{
    private static readonly int[] NominalMinutes = [300, 1440, 10080, 43200];

    /// <summary>
    /// Classifies a window length.
    /// </summary>
    /// <param name="windowMinutes">The length the provider reported.</param>
    public static LimitWindowKind Classify(long windowMinutes) => Normalize(windowMinutes) switch
    {
        300 => LimitWindowKind.FiveHour,
        1440 => LimitWindowKind.Daily,
        10080 => LimitWindowKind.Weekly,
        43200 => LimitWindowKind.Monthly,
        _ => LimitWindowKind.Unknown,
    };

    /// <summary>
    /// Snaps a reported length to the nominal length it is a drifted form of.
    /// </summary>
    /// <param name="windowMinutes">The length the provider reported.</param>
    /// <returns>
    /// The nominal length when one matches within tolerance, otherwise
    /// <paramref name="windowMinutes"/> unchanged. Zero and negative lengths are returned
    /// unchanged, because they are not lengths and callers reject them.
    /// </returns>
    public static long Normalize(long windowMinutes)
    {
        if (windowMinutes <= 0)
        {
            return windowMinutes;
        }

        foreach (int nominal in NominalMinutes)
        {
            long tolerance = Math.Max(5L, nominal / 100L);
            if (Math.Abs(windowMinutes - nominal) <= tolerance)
            {
                return nominal;
            }
        }

        return windowMinutes;
    }

    /// <summary>
    /// The display label for a window length, for example "5 hour" or "Weekly".
    /// </summary>
    /// <param name="windowMinutes">The length the provider reported.</param>
    /// <returns>
    /// A short label. An unrecognised length is described by its own duration rather than
    /// guessed at, so a provider inventing a new window shows up honestly as, say,
    /// "3 hour" instead of being forced into the nearest familiar name.
    /// </returns>
    public static string Label(long windowMinutes)
    {
        long normalized = Normalize(windowMinutes);
        return Classify(normalized) switch
        {
            LimitWindowKind.FiveHour => "5 hour",
            LimitWindowKind.Daily => "Daily",
            LimitWindowKind.Weekly => "Weekly",
            LimitWindowKind.Monthly => "Monthly",
            _ => DescribeDuration(normalized),
        };
    }

    /// <summary>
    /// The key fragment for a window length: the normalised length, formatted invariantly.
    /// </summary>
    /// <param name="windowMinutes">The length the provider reported.</param>
    public static string KeyFragment(long windowMinutes) =>
        Normalize(windowMinutes).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Converts a reported length to a <see cref="TimeSpan"/>.
    /// </summary>
    /// <param name="windowMinutes">
    /// The length the provider reported. Zero or negative yields <see langword="null"/>,
    /// because a window with no length is not a window.
    /// </param>
    public static TimeSpan? ToTimeSpan(long? windowMinutes) =>
        windowMinutes is > 0 ? TimeSpan.FromMinutes(Normalize(windowMinutes.Value)) : null;

    private static string DescribeDuration(long minutes)
    {
        if (minutes <= 0)
        {
            return "Unknown window";
        }

        if (minutes % 1440 == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes / 1440} day");
        }

        if (minutes % 60 == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} hour");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{minutes} min");
    }
}
