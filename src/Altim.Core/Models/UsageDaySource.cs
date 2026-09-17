namespace Altim.Core.Models;

/// <summary>Where a day's row came from, which decides whether a later write may replace it.</summary>
public enum UsageDaySource
{
    /// <summary>Rolled up from readings Altim took itself. Authoritative.</summary>
    Observed = 0,

    /// <summary>Read from a provider's own history. Fills gaps, never overwrites an observed day.</summary>
    Backfilled = 1,
}
