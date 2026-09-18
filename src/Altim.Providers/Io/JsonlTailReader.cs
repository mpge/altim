using System.Buffers;
using System.Text.Json;

namespace Altim.Providers.Io;

/// <summary>
/// Reads a bounded slice of a JSON Lines file and yields only complete lines.
/// </summary>
/// <remarks>
/// <para>
/// The stores this runs against are large: one measured session directory held 28.3 GB
/// across 2,518 files. Nothing here ever reads a whole file or walks a directory tree.
/// A tail read seeks to the end and takes a small window; an incremental read takes
/// only the bytes appended since last time.
/// </para>
/// <para>
/// Two partial-line hazards are handled explicitly. A tail read starts in the middle of
/// a line, so the first fragment is discarded. A file being appended to right now ends
/// in the middle of a line, so the final fragment is discarded and
/// <see cref="JsonlReadResult{T}.EndOffset"/> stops at the last line boundary, letting
/// the next read pick that line up once it is whole.
/// </para>
/// <para>
/// Lines never leave this class. See <see cref="JsonLineParser{T}"/>.
/// </para>
/// </remarks>
public static class JsonlTailReader
{
    /// <summary>
    /// The default tail window. The last rate-limit record in a Codex rollout sits
    /// within about 1.2 KB of end-of-file, so 8 KB reaches it with room to spare.
    /// </summary>
    public const int DefaultTailBytes = 8 * 1024;

    /// <summary>The largest slice any single read will take, as a guard against a pathological file.</summary>
    public const int MaxReadBytes = 8 * 1024 * 1024;

    /// <summary>
    /// How much of a slice is held in memory at once. Lines are parsed out of a window this
    /// size and the window slides forward, so a pass that consumes megabytes of appended
    /// bytes still never holds megabytes to do it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a working-set decision and it was measured.</b> The reader used to rent one
    /// array for the whole slice. Most transcripts are small — on the verification machine
    /// the 96 files a cold scan reads are 37 MB in total with a median of 16 KB — but five of
    /// them are over 85,000 bytes and one is 16.2 MB, and 85,000 bytes is where an array stops
    /// being an ordinary allocation and becomes a large object. Large objects are swept only
    /// by a gen2 collection, <see cref="ArrayPool{T}.Shared"/> holds returned arrays per core
    /// until one runs, and a tray process at idle allocates far too little to cause one.
    /// Measured with dotnet-counters against the real store: a <b>26.8 MB</b> large object
    /// heap, against <b>0.09 MB</b> for the same build pointed at an empty store.
    /// </para>
    /// <para>
    /// 64 KB is under the large-object threshold, so the window itself is never a large
    /// object. A line longer than the window is not skipped: the window doubles until the
    /// line fits or the slice budget is reached, which is the same limit a single-array read
    /// always had. Across those same 96 files, 40 of 13,233 lines are longer than the window
    /// and the longest is 546 KB, so the doubling runs a handful of times in a cold scan and
    /// what is left of the large object heap is 4.2 MB rather than 26.8 MB.
    /// </para>
    /// </remarks>
    private const int WindowBytes = 64 * 1024;

    private const byte LineFeed = 10;
    private const byte CarriageReturn = 13;
    private const byte Space = 32;
    private const byte Tab = 9;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    /// <summary>
    /// Reads the last <paramref name="tailBytes"/> of a file and parses the complete
    /// lines it contains.
    /// </summary>
    /// <typeparam name="T">The value each line reduces to.</typeparam>
    /// <param name="path">The file to read. A missing or locked file yields an empty result.</param>
    /// <param name="parse">The parser that reduces a line to <typeparamref name="T"/>.</param>
    /// <param name="tailBytes">How many bytes to take from the end, clamped to <see cref="MaxReadBytes"/>.</param>
    public static JsonlReadResult<T> ReadTail<T>(string path, JsonLineParser<T> parse, int tailBytes = DefaultTailBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(parse);

        int budget = Math.Clamp(tailBytes, 1, MaxReadBytes);
        return Read(path, startOffset: null, budget, parse);
    }

    /// <summary>
    /// Reads forward from a known line boundary, taking at most <paramref name="maxBytes"/>.
    /// </summary>
    /// <typeparam name="T">The value each line reduces to.</typeparam>
    /// <param name="path">The file to read. A missing or locked file yields an empty result.</param>
    /// <param name="startOffset">
    /// Where to start. Callers pass an offset produced by an earlier
    /// <see cref="JsonlReadResult{T}.EndOffset"/>, which is always a line boundary.
    /// </param>
    /// <param name="parse">The parser that reduces a line to <typeparamref name="T"/>.</param>
    /// <param name="maxBytes">
    /// The byte budget for this read, clamped to <see cref="MaxReadBytes"/>. When the
    /// budget runs out the result is marked <see cref="JsonlReadResult{T}.Truncated"/>
    /// and the caller may read again.
    /// </param>
    public static JsonlReadResult<T> ReadFrom<T>(string path, long startOffset, JsonLineParser<T> parse, long maxBytes = MaxReadBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);

        int budget = (int)Math.Clamp(maxBytes, 1, MaxReadBytes);
        return Read(path, startOffset, budget, parse);
    }

    /// <summary>
    /// Parses one UTF-8 line into a <see cref="JsonDocument"/> for a
    /// <see cref="JsonLineParser{T}"/> implementation.
    /// </summary>
    /// <param name="utf8Line">The line handed to the parser.</param>
    /// <returns>
    /// The document, which the caller disposes, or <see langword="null"/> when the line
    /// is not a JSON object.
    /// </returns>
    /// <exception cref="JsonException">
    /// The line is malformed. <see cref="JsonlTailReader"/> catches this and counts the
    /// line as skipped.
    /// </exception>
    public static JsonDocument? ParseObject(ReadOnlyMemory<byte> utf8Line)
    {
        JsonDocument document = JsonDocument.Parse(utf8Line, DocumentOptions);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    private static JsonlReadResult<T> Read<T>(string path, long? startOffset, int budget, JsonLineParser<T> parse)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,

                    // The provider is very likely appending to this file right now.
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.SequentialScan,
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return JsonlReadResult<T>.Empty;
        }

        using (stream)
        {
            try
            {
                return ReadCore(stream, startOffset, budget, parse);
            }
            catch (IOException)
            {
                return JsonlReadResult<T>.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return JsonlReadResult<T>.Empty;
            }
        }
    }

    private static JsonlReadResult<T> ReadCore<T>(FileStream stream, long? startOffset, int budget, JsonLineParser<T> parse)
    {
        long length = stream.Length;
        if (length <= 0)
        {
            return JsonlReadResult<T>.Empty;
        }

        long start;
        bool startedMidLine;
        if (startOffset is { } explicitStart)
        {
            if (explicitStart >= length)
            {
                return JsonlReadResult<T>.Empty with { EndOffset = explicitStart };
            }

            start = explicitStart;
            startedMidLine = false;
        }
        else
        {
            start = Math.Max(0, length - budget);
            startedMidLine = start > 0;
        }

        long available = length - start;
        int take = (int)Math.Min(available, budget);
        bool truncated = available > take;

        var values = new List<T>();
        int considered = 0;
        int skipped = 0;
        int read = 0;
        int retired = 0;
        bool discardingFragment = startedMidLine;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(take, WindowBytes));
        try
        {
            _ = stream.Seek(start, SeekOrigin.Begin);

            int filled = 0;
            bool exhausted = false;

            while (true)
            {
                if (!exhausted && filled < buffer.Length)
                {
                    int want = (int)Math.Min(buffer.Length - filled, take - read);
                    int got = stream.ReadAtLeast(buffer.AsSpan(filled, want), want, throwOnEndOfStream: false);
                    filled += got;
                    read += got;

                    // Short of what the slice promised means the file is no longer the one
                    // whose length was measured. What was read is still whole lines.
                    exhausted = got < want || read >= take;
                }

                if (read <= 0)
                {
                    return JsonlReadResult<T>.Empty with { EndOffset = start };
                }

                int parsed = ParseWindow(
                    new ReadOnlyMemory<byte>(buffer, 0, filled),
                    ref discardingFragment,
                    parse,
                    values,
                    ref considered,
                    ref skipped);

                retired += parsed;
                filled -= parsed;
                if (filled > 0 && parsed > 0)
                {
                    // Whatever is left is the start of a line the window cut in half. It
                    // goes to the front so the rest of that line can follow it.
                    Buffer.BlockCopy(buffer, parsed, buffer, 0, filled);
                }

                if (exhausted)
                {
                    break;
                }

                if (parsed == 0 && filled == buffer.Length)
                {
                    // A full window with no line break in it: this line is longer than the
                    // window. Grow rather than stall, up to the slice budget, which is the
                    // same ceiling a single-array read had.
                    int wider = Math.Min(take, buffer.Length * 2);
                    if (wider <= buffer.Length)
                    {
                        break;
                    }

                    byte[] grown = ArrayPool<byte>.Shared.Rent(wider);
                    Buffer.BlockCopy(buffer, 0, grown, 0, filled);
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    buffer = grown;
                }
            }

            return new JsonlReadResult<T>(
                values,
                considered,
                skipped,
                read,
                start + retired,
                startedMidLine,
                truncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// Parses every complete line in one window and reports how many of its bytes ended on
    /// a line break.
    /// </summary>
    /// <typeparam name="T">The value each line reduces to.</typeparam>
    /// <param name="window">The bytes currently held.</param>
    /// <param name="discardingFragment">
    /// True while the leading fragment of a tail read has still to be thrown away. Set to
    /// false by the line break that ends it, which may arrive in a later window.
    /// </param>
    /// <param name="parse">The parser that reduces a line to <typeparamref name="T"/>.</param>
    /// <param name="values">Collects what the lines parsed to.</param>
    /// <param name="considered">Counts the non-blank lines handed to the parser.</param>
    /// <param name="skipped">Counts the lines the parser rejected as malformed.</param>
    /// <returns>
    /// How many bytes of the window were consumed. That is the end of the last complete
    /// line, or the whole window while a tail read's leading fragment is still being thrown
    /// away. The caller keeps the rest: it is the beginning of a line the window cut in half.
    /// </returns>
    private static int ParseWindow<T>(
        ReadOnlyMemory<byte> window,
        ref bool discardingFragment,
        JsonLineParser<T> parse,
        List<T> values,
        ref int considered,
        ref int skipped)
    {
        int cursor = 0;

        if (discardingFragment)
        {
            int firstBreak = window.Span.IndexOf(LineFeed);
            if (firstBreak < 0)
            {
                // Still inside the fragment, and none of it is a line. It is consumed
                // rather than kept: the window must not be asked to grow to hold the rest
                // of something that is being thrown away.
                return window.Length;
            }

            cursor = firstBreak + 1;
            discardingFragment = false;
        }

        int lastBoundary = cursor;

        while (cursor < window.Length)
        {
            int relativeBreak = window.Span[cursor..].IndexOf(LineFeed);
            if (relativeBreak < 0)
            {
                // Trailing fragment: the writer is mid-line, or the window ends here.
                // Either way this is not a complete line.
                break;
            }

            int lineStart = cursor;
            int lineEnd = cursor + relativeBreak;
            cursor = lineEnd + 1;
            lastBoundary = cursor;

            if (lineEnd > lineStart && window.Span[lineEnd - 1] == CarriageReturn)
            {
                lineEnd--;
            }

            ReadOnlyMemory<byte> line = window[lineStart..lineEnd];
            if (IsBlank(line.Span))
            {
                continue;
            }

            considered++;
            try
            {
                if (parse(line, out T value))
                {
                    values.Add(value);
                }
            }
            catch (JsonException)
            {
                // A corrupt line is skipped, never fatal: one bad line must not cost
                // the rest of the file.
                skipped++;
            }
        }

        return lastBoundary;
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (byte b in line)
        {
            if (b is not (Space or Tab or CarriageReturn or LineFeed))
            {
                return false;
            }
        }

        return true;
    }
}
