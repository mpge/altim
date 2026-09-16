namespace Altim.Providers.Io;

/// <summary>
/// Remembers where a file was last read to, so a repeat scan reads only the new bytes.
/// </summary>
/// <remarks>
/// <para>
/// A cold scan of the measured Claude Code transcript store was 1.30 GB across 2,080
/// files. Re-reading that on every refresh is not acceptable in a background utility with
/// an idle CPU budget, so a scanner instance lives for the life of the process and hands
/// each file a byte range instead of a filename.
/// </para>
/// <para>
/// Paths are dictionary keys here and nothing else. They are held in memory for the
/// process lifetime, are never persisted, never logged, and never appear on any value
/// this class returns.
/// </para>
/// <para>
/// This type is not thread-safe. The scheduler runs one refresh at a time per provider,
/// which is the only caller.
/// </para>
/// </remarks>
public sealed class IncrementalFileScanner
{
    private readonly Dictionary<string, FileScanCursor> _cursors;
    private readonly long _maxBytesPerFile;

    /// <summary>
    /// Creates a scanner.
    /// </summary>
    /// <param name="maxBytesPerFile">
    /// The most this scanner will read from one file in one pass, clamped to
    /// <see cref="JsonlTailReader.MaxReadBytes"/>. A file that has grown by more than this
    /// is read in pieces across successive passes rather than in one stall.
    /// </param>
    public IncrementalFileScanner(long maxBytesPerFile = JsonlTailReader.MaxReadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytesPerFile, 0);

        _maxBytesPerFile = Math.Min(maxBytesPerFile, JsonlTailReader.MaxReadBytes);
        _cursors = new Dictionary<string, FileScanCursor>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    /// <summary>How many files this scanner is tracking. Diagnostics only; carries no path.</summary>
    public int TrackedFileCount => _cursors.Count;

    /// <summary>
    /// Reads the lines appended to a file since the last committed scan.
    /// </summary>
    /// <typeparam name="T">The value each line reduces to.</typeparam>
    /// <param name="path">The file to scan.</param>
    /// <param name="parse">The parser that reduces a line to <typeparamref name="T"/>.</param>
    /// <returns>
    /// The values parsed out of the new bytes. Empty when the file is unchanged, missing
    /// or unreadable.
    /// </returns>
    /// <remarks>
    /// A file that has shrunk, or whose last-write time has moved backwards, is treated as
    /// replaced and is read from the beginning. That is the correct answer for a rotated
    /// or rewritten session file, and it costs one full read of one file.
    /// </remarks>
    public JsonlReadResult<T> ScanNew<T>(string path, JsonLineParser<T> parse)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(parse);

        if (!TryStat(path, out long length, out long lastWriteTicks))
        {
            _ = _cursors.Remove(path);
            return JsonlReadResult<T>.Empty;
        }

        long startOffset = 0;
        if (_cursors.TryGetValue(path, out FileScanCursor cursor))
        {
            bool replaced = length < cursor.Length || lastWriteTicks < cursor.LastWriteUtcTicks;
            if (replaced)
            {
                startOffset = 0;
            }
            else if (length == cursor.Length && lastWriteTicks == cursor.LastWriteUtcTicks && cursor.Offset >= length)
            {
                // Unchanged, and fully consumed. The offset check matters: a read that hit
                // its byte budget, or stopped short of a half-written trailing line, leaves
                // pending bytes behind in a file whose size and timestamp have not moved.
                return JsonlReadResult<T>.Empty with { EndOffset = cursor.Offset };
            }
            else
            {
                startOffset = Math.Min(cursor.Offset, length);
            }
        }

        JsonlReadResult<T> result = JsonlTailReader.ReadFrom(path, startOffset, parse, _maxBytesPerFile);
        _cursors[path] = new FileScanCursor(length, lastWriteTicks, Math.Max(startOffset, result.EndOffset));
        return result;
    }

    /// <summary>
    /// Stops tracking a file, so the next scan of it starts from the beginning.
    /// </summary>
    /// <param name="path">The file to forget.</param>
    /// <returns>True when the file was being tracked.</returns>
    public bool Forget(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return _cursors.Remove(path);
    }

    /// <summary>
    /// Drops every cursor. The next scan re-reads each file from the beginning.
    /// </summary>
    public void Reset() => _cursors.Clear();

    /// <summary>
    /// Returns the cursor held for a file, for tests and diagnostics.
    /// </summary>
    /// <param name="path">The file to look up.</param>
    /// <param name="cursor">The cursor when the method returns true.</param>
    public bool TryGetCursor(string path, out FileScanCursor cursor)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return _cursors.TryGetValue(path, out cursor);
    }

    private static bool TryStat(string path, out long length, out long lastWriteUtcTicks)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                length = 0;
                lastWriteUtcTicks = 0;
                return false;
            }

            length = info.Length;
            lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            length = 0;
            lastWriteUtcTicks = 0;
            return false;
        }
    }
}
