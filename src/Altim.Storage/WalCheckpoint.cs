namespace Altim.Storage;

/// <summary>
/// What <see cref="AltimDatabase.CheckpointIfIdleAsync"/> did.
/// </summary>
/// <remarks>
/// None of these is a failure. The write-ahead log is bounded by
/// <c>wal_autocheckpoint</c> and <c>journal_size_limit</c> whatever happens here; an idle
/// checkpoint is the tidy-up that takes it the rest of the way to zero, and a pass that
/// could not run leaves a bounded log rather than an unbounded one.
/// </remarks>
public enum WalCheckpoint
{
    /// <summary>Nothing was attempted: the database had been written to too recently.</summary>
    Skipped,

    /// <summary>A reader or a writer held the log, so it was left exactly as it was.</summary>
    Busy,

    /// <summary>The log was emptied into the database and truncated to nothing.</summary>
    Truncated,
}
