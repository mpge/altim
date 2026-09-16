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

        byte[] buffer = ArrayPool<byte>.Shared.Rent(take);
        try
        {
            _ = stream.Seek(start, SeekOrigin.Begin);
            int read = stream.ReadAtLeast(buffer.AsSpan(0, take), take, throwOnEndOfStream: false);
            if (read <= 0)
            {
                return JsonlReadResult<T>.Empty with { EndOffset = start };
            }

            var window = new ReadOnlyMemory<byte>(buffer, 0, read);
            int cursor = 0;

            if (startedMidLine)
            {
                int firstBreak = window.Span.IndexOf(LineFeed);
                if (firstBreak < 0)
                {
                    // The whole window is one unterminated fragment: nothing usable.
                    return JsonlReadResult<T>.Empty with
                    {
                        EndOffset = start,
                        StartedMidLine = true,
                        BytesRead = read,
                        Truncated = truncated,
                    };
                }

                cursor = firstBreak + 1;
            }

            var values = new List<T>();
            int considered = 0;
            int skipped = 0;
            int lastBoundary = cursor;

            while (cursor < read)
            {
                int relativeBreak = window.Span[cursor..].IndexOf(LineFeed);
                if (relativeBreak < 0)
                {
                    // Trailing fragment: the writer is mid-line, or the budget cut the
                    // window here. Either way this is not a complete line.
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

            return new JsonlReadResult<T>(
                values,
                considered,
                skipped,
                read,
                start + lastBoundary,
                startedMidLine,
                truncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
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
