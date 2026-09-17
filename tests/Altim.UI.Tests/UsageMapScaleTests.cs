using Altim.UI.History;
using Xunit;

namespace Altim.UI.Tests;

/// <summary>
/// The map's colour scale ranks days against each other rather than against an absolute
/// token figure.
/// </summary>
/// <remarks>
/// <para>
/// Token counts span orders of magnitude, because a day of heavy cache reads can be fifty
/// times a day of careful work. A linear ramp over that range paints every ordinary day the
/// palest square and the one heavy day black, which is a picture of the outlier and not of
/// the year. So days are ranked and cut into quantiles: the scale is relative to the user's
/// own history, which is the only comparison that carries information.
/// </para>
/// <para>
/// These are plain facts rather than <c>AvaloniaFact</c>s: the scale is arithmetic and must
/// stay testable with no rendering platform anywhere near it.
/// </para>
/// </remarks>
public sealed class UsageMapScaleTests
{
    /// <summary>One enormous day must not flatten the rest of the year into one tone.</summary>
    [Fact]
    public void AnOutlierDoesNotFlattenEveryOtherDay()
    {
        // Nine ordinary days and one enormous one: the ordinary days must still
        // occupy more than one level, which a linear ramp would not manage.
        long[] values = [10, 12, 14, 16, 18, 20, 22, 24, 26, 5_000_000];
        UsageMapScale scale = UsageMapScale.From(values);

        Assert.True(scale.LevelFor(26) > scale.LevelFor(10));
        Assert.Equal(4, scale.LevelFor(5_000_000));
    }

    /// <summary>A day nothing is known about is not a level in the ramp.</summary>
    [Fact]
    public void UnknownIsNotALevel() => Assert.Equal(-1, UsageMapScale.From([1, 2, 3]).LevelFor(null));

    /// <summary>A day that reported zero is the faintest fill, not the outline.</summary>
    [Fact]
    public void ZeroIsTheLowestLevelAndNotUnknown() => Assert.Equal(0, UsageMapScale.From([0, 5, 9]).LevelFor(0));

    /// <summary>Identical days collapse to one bucket instead of producing empty ranges.</summary>
    [Fact]
    public void IdenticalValuesAllLandOnOneLevel()
    {
        UsageMapScale scale = UsageMapScale.From([7, 7, 7, 7]);
        Assert.Equal(scale.LevelFor(7), scale.LevelFor(7));
        Assert.InRange(scale.LevelFor(7), 0, 4);
    }

    /// <summary>A history shorter than the ramp still produces levels that exist.</summary>
    [Fact]
    public void FewerDaysThanLevelsStillProducesValidLevels()
    {
        UsageMapScale scale = UsageMapScale.From([3, 9]);
        Assert.InRange(scale.LevelFor(3), 0, 4);
        Assert.InRange(scale.LevelFor(9), 0, 4);
        Assert.True(scale.LevelFor(9) >= scale.LevelFor(3));
    }

    /// <summary>With nothing to rank against, every day is unknown rather than level zero.</summary>
    [Fact]
    public void NoDataAtAllIsAllUnknown() => Assert.Equal(-1, UsageMapScale.From([]).LevelFor(5));
}
