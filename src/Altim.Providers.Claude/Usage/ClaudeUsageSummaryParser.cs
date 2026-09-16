using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Altim.Providers.Io;
using Altim.Providers.Limits;

namespace Altim.Providers.Claude.Usage;

/// <summary>
/// Reads percentages out of the headless usage summary's human-readable text.
/// </summary>
/// <remarks>
/// <para>
/// This parser is deliberately timid. The summary is prose written for a person, not an
/// interface, and its wording can change in any release. Every pattern is therefore
/// <b>anchored to the start of a line</b> and requires a specific label before it will
/// accept a number, and no pattern will take a percentage from a line it did not recognise.
/// </para>
/// <para>
/// The failure mode that matters is not "no number", it is "the wrong number": a loose
/// pattern that scanned for the first percent sign in the output would happily report a
/// cache hit rate as a quota. A parse failure yields null and the UI says the value is not
/// reported, which is a true statement. A confident wrong number is not.
/// </para>
/// <para>
/// Reset times are not parsed. The summary states them as a wall-clock time with no date and
/// no zone, and a reset instant is never computed or guessed: the status line is the source
/// for those.
/// </para>
/// <para>
/// <b>The exact wording is inferred, not documented.</b> These labels are the ones the
/// summary is expected to use; where the release wording differs, the corresponding metric
/// simply reads as unavailable rather than wrong, which is the behaviour this design is for.
/// </para>
/// </remarks>
public static partial class ClaudeUsageSummaryParser
{
    private const int MaxLinesToScan = 400;
    private const int MaxLineLength = 400;

    /// <summary>
    /// Extracts the <c>result</c> text from the headless JSON envelope.
    /// </summary>
    /// <param name="json">
    /// The output of <c>claude -p --output-format json "/usage"</c>.
    /// </param>
    /// <returns>
    /// The summary parsed out of <c>result</c>, or <see cref="ClaudeUsageSummary.Empty"/>
    /// when the envelope is not JSON, carries no <c>result</c>, or the text matched nothing.
    /// </returns>
    public static ClaudeUsageSummary ParseEnvelope(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return ClaudeUsageSummary.Empty;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("result", out JsonElement result)
                || result.ValueKind is not JsonValueKind.String)
            {
                return ClaudeUsageSummary.Empty;
            }

            // The envelope is also expected to report num_turns 0 and total_cost_usd 0,
            // confirming the call consumed nothing. Those are read only to be discarded:
            // nothing is reported from them.
            _ = JsonValues.ReadInt64(root, "num_turns", "numTurns");

            return ParseText(result.GetString() ?? string.Empty);
        }
    }

    /// <summary>
    /// Parses the summary text itself.
    /// </summary>
    /// <param name="text">The human-readable summary.</param>
    /// <returns>
    /// Whatever was recognised. Unrecognised windows stay null.
    /// </returns>
    public static ClaudeUsageSummary ParseText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        double? session = null;
        double? weekly = null;
        double? opus = null;
        double? sonnet = null;

        int lines = 0;
        foreach (ReadOnlySpan<char> rawLine in text.AsSpan().EnumerateLines())
        {
            if (++lines > MaxLinesToScan)
            {
                break;
            }

            if (rawLine.Length is 0 or > MaxLineLength)
            {
                continue;
            }

            string line = rawLine.ToString();

            // Ordered, and the first pattern to claim a line keeps it. The per-model
            // patterns are tried first so an "Opus" line can never be mistaken for the
            // all-models weekly figure, which would silently understate the real one.
            if (TryClaim(WeeklyOpusPattern(), line, ref opus)
                || TryClaim(WeeklySonnetPattern(), line, ref sonnet)
                || TryClaim(WeeklyAllModelsPattern(), line, ref weekly)
                || TryClaim(SessionPattern(), line, ref session))
            {
                continue;
            }
        }

        return new ClaudeUsageSummary(session, weekly, opus, sonnet);
    }

    [GeneratedRegex(
        @"^\s*(?:Current\s+session|Session)\b[^%\r\n]*?(?<percent>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex SessionPattern();

    [GeneratedRegex(
        @"^\s*(?:Current\s+week|Weekly)(?:\s*\(\s*all\s+models\s*\))?\s*(?::|—|-)?[^%\r\n(]*?(?<percent>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex WeeklyAllModelsPattern();

    [GeneratedRegex(
        @"^\s*(?:Current\s+week|Weekly)\s*\(\s*Opus\s*\)[^%\r\n]*?(?<percent>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex WeeklyOpusPattern();

    [GeneratedRegex(
        @"^\s*(?:Current\s+week|Weekly)\s*\(\s*Sonnet\s*\)[^%\r\n]*?(?<percent>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex WeeklySonnetPattern();

    private static bool TryClaim(Regex pattern, string line, ref double? slot)
    {
        double? value = Match(pattern, line);
        if (value is null)
        {
            return false;
        }

        slot ??= value;
        return true;
    }

    private static double? Match(Regex pattern, string line)
    {
        Match match;
        try
        {
            match = pattern.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological line is not worth a number.
            return null;
        }

        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(match.Groups["percent"].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
            ? PercentReading.Normalize(percent)
            : null;
    }
}
