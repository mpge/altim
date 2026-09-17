namespace Altim.UI.History;

/// <summary>
/// Ranks days against each other and cuts the ranking into the map's colour levels.
/// </summary>
/// <remarks>
/// <para>
/// Token counts span orders of magnitude. Cache reads dwarf everything else, so one day of
/// heavy reading can be fifty times a day of careful work, and a linear ramp over that range
/// paints every ordinary day as the palest square and the one heavy day as black. The picture
/// that produces is a picture of the outlier: it says nothing about the year around it.
/// </para>
/// <para>
/// So the scale is quantile-based. The days that have data are sorted, the ranking is cut into
/// <see cref="Levels"/> equal shares, and a day takes the level its rank falls in. The scale is
/// therefore relative to the user's own history and to nothing else - a level is "heavier than
/// most of your days", never "more than some absolute number of tokens".
/// </para>
/// <para>
/// The cuts are stored as the last value of each share rather than as ranges, which is what
/// makes the awkward inputs fall out instead of having to be special-cased. A cut that repeats
/// the one before it - ties, or fewer days than levels - is dropped, so levels collapse
/// together rather than leaving an empty range between two identical bounds, and no arithmetic
/// ever divides by the width of a range that turned out to be empty. Levels stay contiguous
/// from zero, so a short history uses the bottom of the ramp rather than scattering across it.
/// </para>
/// <para>
/// Nothing here is ever a zero standing in for an absence. A day with no data is
/// <see cref="Unknown"/>, which the map draws as an outline, while a day that reported zero is
/// level zero, the faintest fill - and the two must not be the same square. A scale built from
/// no days at all cannot rank anything, so every day it is asked about is unknown; that is not
/// the same as a one-level scale, which has data and answers zero.
/// </para>
/// </remarks>
public sealed class UsageMapScale
{
    /// <summary>The level of a day nothing is known about. Not a step in the ramp.</summary>
    public const int Unknown = -1;

    /// <summary>
    /// The last value of each level's share of the ranking, ascending and distinct. A value
    /// belongs to the level that counts how many of these it is above.
    /// </summary>
    private readonly long[] _cuts;

    private UsageMapScale(int levels, long[] cuts, bool isEmpty)
    {
        Levels = levels;
        _cuts = cuts;
        IsEmpty = isEmpty;
    }

    /// <summary>How many steps the ramp has. A level is always <c>0</c> to this less one.</summary>
    public int Levels { get; }

    /// <summary>
    /// Whether the scale was built from no days at all, in which case it ranks nothing and
    /// every day is <see cref="Unknown"/>.
    /// </summary>
    public bool IsEmpty { get; }

    /// <summary>
    /// Builds a scale by ranking the values that are known.
    /// </summary>
    /// <param name="values">
    /// One value per day that has data. Days with no data are left out rather than passed in as
    /// zeroes: a zero would drag every cut down and quietly re-colour the days that do have
    /// data.
    /// </param>
    /// <param name="levels">How many steps the ramp has.</param>
    /// <returns>
    /// A scale over those values. An empty list gives a scale that answers
    /// <see cref="Unknown"/> for everything, because there is nothing to rank against.
    /// </returns>
    public static UsageMapScale From(IReadOnlyList<long> values, int levels = 5)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);

        if (values.Count == 0)
        {
            return new UsageMapScale(levels, [], isEmpty: true);
        }

        long[] sorted = [.. values];
        Array.Sort(sorted);

        List<long> cuts = [];
        for (int level = 0; level < levels - 1; level++)
        {
            // The rank at which this level's share ends. Rounding the share up keeps the cut
            // inside the ranking when there are fewer days than levels, where rounding down
            // would ask for rank zero repeatedly and cut nothing at all.
            int rank = (int)((((long)level + 1) * sorted.Length + levels - 1) / levels) - 1;
            long cut = sorted[rank];

            // Ties: this share ends on the same value as the last one, so the level between
            // them could only ever hold values that do not exist. Drop it rather than keep an
            // empty range.
            if (cuts.Count == 0 || cuts[^1] != cut)
            {
                cuts.Add(cut);
            }
        }

        return new UsageMapScale(levels, [.. cuts], isEmpty: false);
    }

    /// <summary>
    /// The ramp level for one day's value.
    /// </summary>
    /// <param name="value">The day's total tokens, or <see langword="null"/> when unknown.</param>
    /// <returns>
    /// <see cref="Unknown"/> for a day with no value, and for any day when the scale ranks
    /// nothing. Otherwise a level from <c>0</c> to <see cref="Levels"/> less one: the number of
    /// cuts the value stands above, which is the highest level whose share it reaches.
    /// </returns>
    /// <remarks>
    /// The value need not be one of the values the scale was built from. A value above every
    /// day seen takes the top level and one below every day seen takes the bottom, which is
    /// what lets a combined row be ranked on the same scale as the rows it sums.
    /// </remarks>
    public int LevelFor(long? value)
    {
        if (IsEmpty || value is not { } known)
        {
            return Unknown;
        }

        int level = 0;
        while (level < _cuts.Length && known > _cuts[level])
        {
            level++;
        }

        return level;
    }
}
