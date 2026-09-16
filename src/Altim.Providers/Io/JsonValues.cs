using System.Globalization;
using System.Text.Json;

namespace Altim.Providers.Io;

/// <summary>
/// The only way provider readers take a value out of a JSON document.
/// </summary>
/// <remarks>
/// <para>
/// Every accessor here returns a number, a timestamp, a boolean or a short identifier.
/// There is deliberately no "read this string" helper, because the files these readers
/// open contain prompts, source code, command output and repository URLs, and a general
/// string accessor is all it would take for one of those to escape.
/// </para>
/// <para>
/// <see cref="ReadIdentifier(in JsonElement, string, string?)"/> is the one string path,
/// and it is narrow by construction: it rejects anything longer than
/// <see cref="MaxIdentifierLength"/> and anything containing a character outside the set
/// identifiers use, which excludes whitespace, path separators, quotes and URL
/// punctuation. A file path, a URL or a sentence of prose cannot survive it.
/// </para>
/// <para>
/// Every accessor is tolerant: a missing property, a null, or a value of the wrong kind
/// all produce <see langword="null"/>. Vendors disclaim these formats, so an unexpected
/// shape must degrade one field to unavailable rather than fail a read.
/// </para>
/// </remarks>
public static class JsonValues
{
    /// <summary>The longest string <see cref="ReadIdentifier(in JsonElement, string, string?)"/> will return.</summary>
    public const int MaxIdentifierLength = 96;

    /// <summary>
    /// The latest instant a Unix-seconds field is allowed to describe, as seconds.
    /// Corresponds to 2100-01-01. Anything beyond it is treated as a wrong-units value
    /// (milliseconds in a seconds field) rather than a date centuries away.
    /// </summary>
    private const long MaxUnixSeconds = 4_102_444_800L;

    /// <summary>
    /// The longest string <see cref="ReadLooseDouble"/> will even attempt to parse. A
    /// number does not need more, and a longer one is prose that must not be touched.
    /// </summary>
    private const int MaxNumericStringLength = 32;

    /// <summary>
    /// Reads a floating-point number from the first of up to two spellings that is present.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name, for example <c>used_percent</c>.</param>
    /// <param name="alternateName">
    /// The other dialect's spelling, for example <c>usedPercent</c>.
    /// <see langword="null"/> when the concept has only one spelling.
    /// </param>
    /// <returns>The value, or <see langword="null"/> when absent, null, or not a number.</returns>
    public static double? ReadDouble(in JsonElement parent, string name, string? alternateName = null)
    {
        if (TryGetProperty(parent, name, alternateName, out JsonElement value)
            && value.ValueKind is JsonValueKind.Number
            && value.TryGetDouble(out double result)
            && double.IsFinite(result))
        {
            return result;
        }

        return null;
    }

    /// <summary>
    /// Reads a whole number from the first of up to two spellings that is present.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name.</param>
    /// <param name="alternateName">The other dialect's spelling, or <see langword="null"/>.</param>
    /// <returns>The value, or <see langword="null"/> when absent, null, or not a whole number.</returns>
    public static long? ReadInt64(in JsonElement parent, string name, string? alternateName = null)
    {
        if (!TryGetProperty(parent, name, alternateName, out JsonElement value) || value.ValueKind is not JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt64(out long whole))
        {
            return whole;
        }

        // Some writers emit a whole count as a float. Accept it when it is exactly integral.
        if (value.TryGetDouble(out double approximate)
            && double.IsFinite(approximate)
            && approximate >= long.MinValue
            && approximate <= long.MaxValue
            && Math.Abs(approximate - Math.Round(approximate)) < 0.0001d)
        {
            return (long)Math.Round(approximate);
        }

        return null;
    }

    /// <summary>
    /// Reads a non-negative token count. Negative values are rejected rather than clamped,
    /// because a negative count means the field was not what it looked like.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name.</param>
    /// <param name="alternateName">The other dialect's spelling, or <see langword="null"/>.</param>
    public static long? ReadCount(in JsonElement parent, string name, string? alternateName = null)
    {
        long? value = ReadInt64(parent, name, alternateName);
        return value is >= 0 ? value : null;
    }

    /// <summary>
    /// Reads a boolean, tolerating a provider that writes one as a number.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name.</param>
    /// <param name="alternateName">The other dialect's spelling, or <see langword="null"/>.</param>
    public static bool? ReadBoolean(in JsonElement parent, string name, string? alternateName = null)
    {
        if (!TryGetProperty(parent, name, alternateName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out long number) => number != 0,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a number that a provider may have written as a JSON string.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name.</param>
    /// <param name="alternateName">The other dialect's spelling, or <see langword="null"/>.</param>
    /// <returns>
    /// The value, or <see langword="null"/> when absent or when the string is not entirely
    /// a number. Nothing but the parsed number leaves this method: a string that does not
    /// parse is discarded rather than carried.
    /// </returns>
    /// <remarks>
    /// Money-shaped fields arrive as strings. The Codex credit balance is declared as
    /// <c>string | null</c> and arrives as <c>"0"</c>, so a reader that accepted only JSON
    /// numbers would report "no credit balance" for every account that has one.
    /// </remarks>
    public static double? ReadLooseDouble(in JsonElement parent, string name, string? alternateName = null)
    {
        if (!TryGetProperty(parent, name, alternateName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number))
        {
            return number;
        }

        if (value.ValueKind is JsonValueKind.String)
        {
            string? text = value.GetString();
            if (text is not null
                && text.Length <= MaxNumericStringLength
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && double.IsFinite(parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an instant recorded as a Unix timestamp in either seconds or milliseconds.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name.</param>
    /// <param name="alternateName">The other dialect's spelling, or <see langword="null"/>.</param>
    /// <returns>The instant, or <see langword="null"/> when absent or implausible.</returns>
    /// <remarks>
    /// The units are decided by magnitude rather than by the property name, because the
    /// same property name carries both: <c>claude agents --json</c> reports
    /// <c>startedAt</c> as Unix milliseconds while the status-line payload reports
    /// <c>resets_at</c> as Unix seconds. A seconds-only reader silently discards every
    /// millisecond timestamp as "beyond the year 2100", which reads downstream as "the
    /// provider did not report a start time".
    /// </remarks>
    public static DateTimeOffset? ReadUnixTimestamp(in JsonElement parent, string name, string? alternateName = null)
    {
        long? raw = ReadInt64(parent, name, alternateName);
        if (raw is not { } value || value <= 0)
        {
            return null;
        }

        try
        {
            return value > MaxUnixSeconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Milliseconds so large they are not a date either.
            return null;
        }
    }

    /// <summary>
    /// Reads an instant recorded as Unix seconds.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name, for example <c>resets_at</c>.</param>
    /// <param name="alternateName">The other dialect's spelling, for example <c>resetsAt</c>.</param>
    /// <returns>
    /// The instant, or <see langword="null"/> when absent or outside the plausible range.
    /// A reset instant that is not reported stays unknown: it is never computed from a
    /// window length.
    /// </returns>
    public static DateTimeOffset? ReadUnixSeconds(in JsonElement parent, string name, string? alternateName = null)
    {
        long? seconds = ReadInt64(parent, name, alternateName);
        if (seconds is not { } value || value <= 0 || value > MaxUnixSeconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(value);
    }

    /// <summary>
    /// Reads an ISO 8601 instant.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name, for example <c>timestamp</c>.</param>
    /// <param name="alternateName">The other dialect's spelling, or <see langword="null"/>.</param>
    /// <returns>The instant in UTC, or <see langword="null"/> when absent or unparseable.</returns>
    public static DateTimeOffset? ReadIso8601(in JsonElement parent, string name, string? alternateName = null)
    {
        if (!TryGetProperty(parent, name, alternateName, out JsonElement value) || value.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        if (value.TryGetDateTimeOffset(out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    /// <summary>
    /// Reads a short identifier such as a message id, a session id, a model id, a limit
    /// id or a plan name.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name.</param>
    /// <param name="alternateName">The other dialect's spelling, or <see langword="null"/>.</param>
    /// <returns>
    /// The identifier, or <see langword="null"/> when the property is absent, is not a
    /// string, is longer than <see cref="MaxIdentifierLength"/>, or contains a character
    /// no identifier contains. The rejection is the point: it is what stops a path, a URL
    /// or a line of prose from being read out of a transcript by a mistyped property name.
    /// </returns>
    public static string? ReadIdentifier(in JsonElement parent, string name, string? alternateName = null)
    {
        if (!TryGetProperty(parent, name, alternateName, out JsonElement value) || value.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return IsIdentifier(text) ? text : null;
    }

    /// <summary>
    /// True when <paramref name="text"/> looks like an identifier and is therefore safe to
    /// carry out of a reader.
    /// </summary>
    /// <param name="text">The candidate.</param>
    /// <remarks>
    /// Permits letters, digits, <c>-</c>, <c>_</c>, <c>.</c>, <c>+</c>, <c>@</c> and square
    /// brackets, which covers message ids, UUIDs, plan names and model ids including the
    /// <c>[1m]</c> long-context suffix. It rejects whitespace, slashes, backslashes, colons
    /// and quotes, which is what excludes paths, URLs and prose.
    /// </remarks>
    public static bool IsIdentifier(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxIdentifierLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            bool permitted = char.IsAsciiLetterOrDigit(c)
                || c is '-' or '_' or '.' or '+' or '@' or '[' or ']';
            if (!permitted)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets a child object by the first of up to two spellings that is present.
    /// </summary>
    /// <param name="parent">The object to read from.</param>
    /// <param name="name">The preferred property name.</param>
    /// <param name="alternateName">The other dialect's spelling, or <see langword="null"/>.</param>
    /// <param name="child">The child object when the method returns true.</param>
    /// <returns>True when a child object of that name exists. A JSON null returns false.</returns>
    public static bool TryGetObject(in JsonElement parent, string name, string? alternateName, out JsonElement child)
    {
        if (TryGetProperty(parent, name, alternateName, out JsonElement value) && value.ValueKind is JsonValueKind.Object)
        {
            child = value;
            return true;
        }

        child = default;
        return false;
    }

    /// <summary>
    /// Formats a whole number for a metric key, invariantly, so a key never varies by
    /// the machine's locale.
    /// </summary>
    /// <param name="value">The number to format.</param>
    public static string FormatInvariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryGetProperty(in JsonElement parent, string name, string? alternateName, out JsonElement value)
    {
        if (parent.ValueKind is JsonValueKind.Object)
        {
            if (parent.TryGetProperty(name, out value) && value.ValueKind is not JsonValueKind.Null)
            {
                return true;
            }

            if (alternateName is not null && parent.TryGetProperty(alternateName, out value) && value.ValueKind is not JsonValueKind.Null)
            {
                return true;
            }
        }

        value = default;
        return false;
    }
}
