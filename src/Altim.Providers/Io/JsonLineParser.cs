namespace Altim.Providers.Io;

/// <summary>
/// Turns one raw JSON line into a small value, inside the reader.
/// </summary>
/// <remarks>
/// This delegate is the privacy boundary. <see cref="JsonlTailReader"/> never returns a
/// line, a string or a <see cref="System.Text.Json.JsonDocument"/> to its caller: it
/// hands each line to a parser, keeps the value the parser produced, and lets the
/// buffer go. A caller therefore cannot receive prompt text, source code, command
/// output or a project path even by accident, because no code path exists that would
/// carry one out.
/// </remarks>
/// <typeparam name="T">
/// The small value the line reduces to. Implementations return numeric, timestamp and
/// short identifier fields only.
/// </typeparam>
/// <param name="utf8Line">
/// The line's UTF-8 bytes, without its terminator. Valid only for the duration of the
/// call: it points into a pooled buffer that is reused immediately afterwards, so an
/// implementation copies anything it wants to keep.
/// </param>
/// <param name="value">The parsed value when the method returns true.</param>
/// <returns>
/// True when the line was understood. False when it was a line of a shape this parser
/// does not care about, which is the normal case and not an error.
/// </returns>
public delegate bool JsonLineParser<T>(ReadOnlyMemory<byte> utf8Line, out T value);
