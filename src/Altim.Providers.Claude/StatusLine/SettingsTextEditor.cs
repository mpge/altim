using System.Text;
using System.Text.Json;

namespace Altim.Providers.Claude.StatusLine;

/// <summary>
/// Adds and removes one top-level property of a JSON settings file by editing its text.
/// </summary>
/// <remarks>
/// <para>
/// The alternative — parse, then write the document back out from the parse — is correct
/// JSON and is not the same file. It loses comments, which the settings format allows and
/// people use to explain their own configuration, and it re-flows every line, so a one-line
/// change arrives as a whole-file diff in the user's dotfiles repository.
/// </para>
/// <para>
/// This edits the smallest span that does the job and copies every other byte through
/// untouched, including the byte-order mark, the line endings and the indentation.
/// </para>
/// <para>
/// Every edit is validated by re-parsing before it is offered to the caller. An edit that
/// does not produce a settings file with exactly the intended change is refused, and the
/// caller falls back to reserialising, which is uglier and always correct.
/// </para>
/// </remarks>
internal static class SettingsTextEditor
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Inserts a property as the first member of the root object.
    /// </summary>
    /// <param name="utf8">The file as it is on disk.</param>
    /// <param name="propertyName">The property to add. Must not already be present.</param>
    /// <param name="valueJson">
    /// The property's value, already JSON, indented relative to column zero. It is re-indented
    /// to sit under the root object.
    /// </param>
    /// <param name="edited">The edited file when the method returns true.</param>
    /// <returns>False when the file's shape was not one this editor is sure of.</returns>
    public static bool TryInsert(byte[] utf8, string propertyName, string valueJson, out byte[] edited)
    {
        edited = [];

        if (!TryLocateRoot(utf8, propertyName, out Location location))
        {
            return false;
        }

        if (location.PropertyFound)
        {
            return false;
        }

        string newline = DetectNewline(utf8);
        string indent = DetectIndent(utf8, location.BodyStart) ?? "  ";
        string value = Reindent(valueJson, indent, newline);

        var insertion = new StringBuilder();
        _ = insertion.Append(newline).Append(indent).Append('"').Append(Escape(propertyName)).Append("\": ").Append(value);
        if (location.HasProperties)
        {
            _ = insertion.Append(',');
        }

        byte[] candidate = Splice(utf8, location.BodyStart, location.BodyStart, Encoding.UTF8.GetBytes(insertion.ToString()));
        if (!Verify(candidate, utf8, propertyName, shouldBePresent: true))
        {
            return false;
        }

        edited = candidate;
        return true;
    }

    /// <summary>
    /// Removes a top-level property, together with the comma and the blank line it leaves.
    /// </summary>
    /// <param name="utf8">The file as it is on disk.</param>
    /// <param name="propertyName">The property to remove.</param>
    /// <param name="edited">The edited file when the method returns true.</param>
    /// <returns>False when the property is absent or the shape was not one this editor is sure of.</returns>
    public static bool TryRemove(byte[] utf8, string propertyName, out byte[] edited)
    {
        edited = [];

        if (!TryLocateRoot(utf8, propertyName, out Location location) || !location.PropertyFound)
        {
            return false;
        }

        int start = location.PropertyStart;
        int end = location.PropertyEnd;

        // Take the trailing comma with it, then the rest of the line, so the property does
        // not leave a blank line behind.
        int afterValue = SkipSpaces(utf8, end);
        if (afterValue < utf8.Length && utf8[afterValue] == (byte)',')
        {
            end = afterValue + 1;
        }

        int lineStart = StartOfLine(utf8, start);
        bool aloneOnItsLine = IsBlank(utf8, lineStart, start);
        if (aloneOnItsLine)
        {
            start = lineStart;

            int afterLine = SkipSpaces(utf8, end);
            if (afterLine < utf8.Length && utf8[afterLine] == (byte)'\r')
            {
                afterLine++;
            }

            if (afterLine < utf8.Length && utf8[afterLine] == (byte)'\n')
            {
                end = afterLine + 1;
            }
        }

        // It was the last property: the comma that separated it from the previous one is
        // now a trailing comma, so that goes too.
        int beforeStart = SkipSpacesBackwards(utf8, start);
        if (beforeStart > 0 && utf8[beforeStart - 1] == (byte)',' && NextIsClose(utf8, end))
        {
            start = beforeStart - 1;
        }

        byte[] candidate = Splice(utf8, start, end, []);
        if (!Verify(candidate, utf8, propertyName, shouldBePresent: false))
        {
            return false;
        }

        edited = candidate;
        return true;
    }

    private static bool TryLocateRoot(byte[] utf8, string propertyName, out Location location)
    {
        location = default;

        int offset = StartsWithBom(utf8) ? Utf8ByteOrderMark.Length : 0;
        var reader = new Utf8JsonReader(utf8.AsSpan(offset), new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64,
        });

        try
        {
            if (!reader.Read() || reader.TokenType is not JsonTokenType.StartObject)
            {
                return false;
            }

            int bodyStart = offset + (int)reader.TokenStartIndex + 1;
            bool hasProperties = false;
            bool found = false;
            int propertyStart = 0;
            int propertyEnd = 0;

            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    break;
                }

                if (reader.TokenType is not JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                hasProperties = true;
                bool match = reader.ValueTextEquals(propertyName);
                int start = offset + (int)reader.TokenStartIndex;

                if (!reader.Read())
                {
                    return false;
                }

                reader.Skip();
                int end = offset + (int)reader.BytesConsumed;

                if (match)
                {
                    found = true;
                    propertyStart = start;
                    propertyEnd = end;
                }
            }

            location = new Location(bodyStart, hasProperties, found, propertyStart, propertyEnd);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Confirms an edit did exactly what it was meant to: the property is present or gone,
    /// and every other top-level property survived with its value unchanged.
    /// </summary>
    private static bool Verify(byte[] candidate, byte[] original, string propertyName, bool shouldBePresent)
    {
        try
        {
            using JsonDocument edited = JsonDocument.Parse(Body(candidate), ParseOptions);
            using JsonDocument before = JsonDocument.Parse(Body(original), ParseOptions);

            if (edited.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            if (edited.RootElement.TryGetProperty(propertyName, out _) != shouldBePresent)
            {
                return false;
            }

            foreach (JsonProperty property in before.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!edited.RootElement.TryGetProperty(property.Name, out JsonElement kept)
                    || !string.Equals(kept.GetRawText(), property.Value.GetRawText(), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ReadOnlyMemory<byte> Body(byte[] utf8) =>
        StartsWithBom(utf8) ? utf8.AsMemory(Utf8ByteOrderMark.Length) : utf8.AsMemory();

    private static bool StartsWithBom(byte[] utf8) =>
        utf8.Length >= 3 && utf8[0] == Utf8ByteOrderMark[0] && utf8[1] == Utf8ByteOrderMark[1] && utf8[2] == Utf8ByteOrderMark[2];

    private static byte[] Splice(byte[] utf8, int start, int end, byte[] insertion)
    {
        byte[] result = new byte[start + insertion.Length + (utf8.Length - end)];
        Array.Copy(utf8, 0, result, 0, start);
        Array.Copy(insertion, 0, result, start, insertion.Length);
        Array.Copy(utf8, end, result, start + insertion.Length, utf8.Length - end);
        return result;
    }

    private static string DetectNewline(byte[] utf8)
    {
        for (int i = 0; i + 1 < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'\n')
            {
                return i > 0 && utf8[i - 1] == (byte)'\r' ? "\r\n" : "\n";
            }
        }

        return Environment.NewLine;
    }

    /// <summary>The indentation of the first line inside the root object, when it has one.</summary>
    private static string? DetectIndent(byte[] utf8, int bodyStart)
    {
        int i = bodyStart;
        while (i < utf8.Length && utf8[i] is (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        int start = i;
        while (i < utf8.Length && utf8[i] is (byte)' ' or (byte)'\t')
        {
            i++;
        }

        return i > start ? Encoding.UTF8.GetString(utf8, start, i - start) : null;
    }

    private static string Reindent(string valueJson, string indent, string newline)
    {
        string[] lines = valueJson.ReplaceLineEndings("\n").Split('\n');
        var builder = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                _ = builder.Append(newline).Append(indent);
            }

            _ = builder.Append(lines[i]);
        }

        return builder.ToString();
    }

    private static string Escape(string text) => System.Text.Json.JsonEncodedText.Encode(text).ToString();

    private static int SkipSpaces(byte[] utf8, int index)
    {
        while (index < utf8.Length && utf8[index] is (byte)' ' or (byte)'\t')
        {
            index++;
        }

        return index;
    }

    private static int SkipSpacesBackwards(byte[] utf8, int index)
    {
        while (index > 0 && utf8[index - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index--;
        }

        return index;
    }

    private static int StartOfLine(byte[] utf8, int index)
    {
        while (index > 0 && utf8[index - 1] is not ((byte)'\n' or (byte)'\r'))
        {
            index--;
        }

        return index;
    }

    private static bool IsBlank(byte[] utf8, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (utf8[i] is not ((byte)' ' or (byte)'\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool NextIsClose(byte[] utf8, int index)
    {
        while (index < utf8.Length && utf8[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }

        return index < utf8.Length && utf8[index] == (byte)'}';
    }

    private readonly record struct Location(
        int BodyStart,
        bool HasProperties,
        bool PropertyFound,
        int PropertyStart,
        int PropertyEnd);
}
