namespace Altim.Providers.Io;

/// <summary>
/// What <see cref="IncrementalFileScanner"/> remembers about one file so it can read
/// only what has been appended since last time.
/// </summary>
/// <param name="Length">The file's length in bytes at the last commit.</param>
/// <param name="LastWriteUtcTicks">
/// The file's last-write time at the last commit, in UTC ticks. Stored as ticks rather
/// than a <see cref="DateTimeOffset"/> so equality is exact.
/// </param>
/// <param name="Offset">
/// The byte offset the next read resumes at. Always a line boundary, so a line that was
/// still being written at the last read is re-read whole rather than lost.
/// </param>
public readonly record struct FileScanCursor(long Length, long LastWriteUtcTicks, long Offset);
