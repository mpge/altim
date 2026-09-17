namespace Altim.Core.Models;

/// <summary>
/// Where a day's <b>token figure</b> came from, which is what the map draws and what the
/// tooltip names. It ranks nothing: a write merges into the stored row field by field, and
/// this travels with the tokens rather than deciding who may overwrite whom.
/// </summary>
/// <remarks>
/// A row that has never carried tokens keeps whichever value it was created with, and says
/// nothing about a figure it does not have.
/// </remarks>
public enum UsageDaySource
{
    /// <summary>
    /// Written by the rollup over readings Altim took itself. Those readings are running
    /// totals rather than per-day amounts, so a rolled-up row carries no tokens at all.
    /// </summary>
    Observed = 0,

    /// <summary>
    /// Read from a provider's own per-day history. This is the only source of a day's token
    /// figure; a later backfill replaces an earlier one.
    /// </summary>
    Backfilled = 1,
}
