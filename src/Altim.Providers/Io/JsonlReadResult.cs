namespace Altim.Providers.Io;

/// <summary>
/// What one bounded read of a JSON Lines file produced.
/// </summary>
/// <typeparam name="T">The value type the line parser produced.</typeparam>
/// <param name="Values">
/// The parsed values, in file order. Empty when the file was missing, unreadable, or
/// contained no line this parser recognised; those three cases are deliberately not
/// distinguished, because none of them is an error.
/// </param>
/// <param name="LinesConsidered">
/// How many complete lines were offered to the parser, including ones it declined.
/// </param>
/// <param name="LinesSkipped">
/// How many complete lines were malformed JSON and were skipped. A corrupt line in the
/// middle of a file is survivable, so this is a count rather than a failure.
/// </param>
/// <param name="BytesRead">How many bytes were read from the file.</param>
/// <param name="EndOffset">
/// The absolute byte offset just past the last complete line consumed. A later read
/// resuming here starts on a line boundary, so a partially written trailing line is
/// re-read once it is complete rather than lost.
/// </param>
/// <param name="StartedMidLine">
/// True when the read began inside a line, so the first partial line was discarded.
/// Always true for a tail read of a file longer than the tail window.
/// </param>
/// <param name="Truncated">
/// True when the read stopped at its byte budget with more of the file left. The
/// caller may read again from <paramref name="EndOffset"/>.
/// </param>
public sealed record JsonlReadResult<T>(
    IReadOnlyList<T> Values,
    int LinesConsidered,
    int LinesSkipped,
    long BytesRead,
    long EndOffset,
    bool StartedMidLine,
    bool Truncated)
{
    /// <summary>An empty result for a file that could not be read at all.</summary>
    public static JsonlReadResult<T> Empty { get; } =
        new([], 0, 0, 0, 0, false, false);
}
