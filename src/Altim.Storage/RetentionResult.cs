namespace Altim.Storage;

/// <summary>
/// What one down-sampling run changed.
/// </summary>
/// <param name="CollapsedRows">
/// How many full-resolution rows were removed. Zero means there was nothing old enough,
/// or nothing left to collapse, which is the normal result of a repeat run.
/// </param>
/// <param name="RetainedRows">
/// How many hourly rows replaced them. Never larger than
/// <paramref name="CollapsedRows"/>.
/// </param>
public readonly record struct RetentionResult(int CollapsedRows, int RetainedRows)
{
    /// <summary>
    /// True when the run left the database exactly as it found it.
    /// </summary>
    public bool ChangedNothing => CollapsedRows == 0 && RetainedRows == 0;
}
