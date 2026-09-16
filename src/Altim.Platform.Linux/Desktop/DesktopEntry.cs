using System.Text;

namespace Altim.Platform.Linux.Desktop;

/// <summary>
/// Reads and writes the XDG desktop entry that registers Altim to start with the session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Disabling means <c>Hidden=true</c>, not deleting the file.</b> The Desktop Entry
/// specification defines <c>Hidden</c> as "the user deleted this entry at their level", and
/// every autostart implementation honours it. Removing the file instead looks equivalent
/// until a system-wide <c>/etc/xdg/autostart/altim.desktop</c> exists: deleting the user's
/// copy then un-masks the system one and Altim starts anyway, which is the opposite of what
/// the user asked for. Writing <c>Hidden=true</c> masks it properly.
/// </para>
/// <para>
/// <c>X-GNOME-Autostart-enabled=false</c> is honoured on read for the same reason: GNOME
/// Tweaks and some session managers write that instead, and an entry a user switched off
/// there must not be reported as enabled.
/// </para>
/// <para>
/// Everything here is pure text handling over a string, so it is tested directly rather than
/// against a real home directory.
/// </para>
/// </remarks>
public static class DesktopEntry
{
    /// <summary>The only group a desktop entry's keys are read from.</summary>
    public const string GroupName = "Desktop Entry";

    /// <summary>The key that masks an entry.</summary>
    public const string HiddenKey = "Hidden";

    /// <summary>GNOME's own disable key, honoured on read.</summary>
    public const string GnomeEnabledKey = "X-GNOME-Autostart-enabled";

    /// <summary>
    /// Characters the Desktop Entry specification reserves inside an <c>Exec</c> value.
    /// </summary>
    private const string ExecEscapedCharacters = "\"`$\\";

    /// <summary>
    /// Builds a complete autostart entry.
    /// </summary>
    /// <param name="name">The name shown by session managers, for example <c>Altim</c>.</param>
    /// <param name="comment">One sentence of description. Empty omits the key.</param>
    /// <param name="executablePath">The absolute path of the executable to launch.</param>
    /// <param name="iconName">
    /// An icon theme name or absolute icon path. Empty omits the key.
    /// </param>
    /// <param name="hidden">
    /// True writes the entry in its disabled state. Altim writes <c>false</c> explicitly
    /// rather than omitting the key, so toggling later is an edit rather than an insert.
    /// </param>
    /// <returns>The file contents, with Unix line endings and a trailing newline.</returns>
    public static string Build(
        string name, string comment, string executablePath, string iconName = "", bool hidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(iconName);

        var builder = new StringBuilder();
        _ = builder.Append('[').Append(GroupName).Append("]\n");
        _ = builder.Append("Type=Application\n");
        _ = builder.Append("Name=").Append(EscapeValue(name)).Append('\n');

        if (comment.Length > 0)
        {
            _ = builder.Append("Comment=").Append(EscapeValue(comment)).Append('\n');
        }

        _ = builder.Append("Exec=").Append(QuoteExec(executablePath)).Append('\n');

        if (iconName.Length > 0)
        {
            _ = builder.Append("Icon=").Append(EscapeValue(iconName)).Append('\n');
        }

        // Deliberately no NoDisplay key: an autostart entry that hides itself also
        // disappears from GNOME Tweaks' startup list, which is where a user goes to switch
        // it off.
        _ = builder.Append("Terminal=false\n");
        _ = builder.Append(HiddenKey).Append('=').Append(hidden ? "true" : "false").Append('\n');
        _ = builder.Append(GnomeEnabledKey).Append('=').Append(hidden ? "false" : "true").Append('\n');

        return builder.ToString();
    }

    /// <summary>
    /// Whether an existing entry will actually start.
    /// </summary>
    /// <param name="contents">The file contents.</param>
    /// <returns>
    /// False when <c>Hidden</c> is true or <c>X-GNOME-Autostart-enabled</c> is false, and
    /// true otherwise — including when neither key is present, because an entry with no
    /// opinion runs.
    /// </returns>
    public static bool IsEnabled(string? contents)
    {
        if (string.IsNullOrWhiteSpace(contents))
        {
            return false;
        }

        if (ReadBoolean(contents, HiddenKey) == true)
        {
            return false;
        }

        return ReadBoolean(contents, GnomeEnabledKey) != false;
    }

    /// <summary>
    /// Reads one boolean key out of the <c>[Desktop Entry]</c> group.
    /// </summary>
    /// <param name="contents">The file contents.</param>
    /// <param name="key">The key name, compared case-sensitively as the specification requires.</param>
    /// <returns>
    /// The value, or <see langword="null"/> when the key is absent from the group or does
    /// not hold <c>true</c> or <c>false</c>. Keys outside <c>[Desktop Entry]</c> are ignored
    /// entirely, which is what stops an <c>[Desktop Action …]</c> group from answering for
    /// the entry.
    /// </returns>
    public static bool? ReadBoolean(string? contents, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (string.IsNullOrEmpty(contents))
        {
            return null;
        }

        bool? result = null;
        bool inGroup = false;

        foreach (string rawLine in EnumerateLines(contents))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                inGroup = line.Equals("[" + GroupName + "]", StringComparison.Ordinal);
                continue;
            }

            if (!inGroup)
            {
                continue;
            }

            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || !line[..separator].Trim().Equals(key, StringComparison.Ordinal))
            {
                continue;
            }

            string value = line[(separator + 1)..].Trim();

            // A duplicate key is malformed; taking the last one matches what the reference
            // desktop-file library does.
            result = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? true
                : value.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : result;
        }

        return result;
    }

    /// <summary>
    /// Returns the entry with its <c>Hidden</c> and <c>X-GNOME-Autostart-enabled</c> keys set,
    /// leaving every other line exactly as it was.
    /// </summary>
    /// <param name="contents">The existing file contents.</param>
    /// <param name="hidden">True to mask the entry, false to un-mask it.</param>
    /// <returns>
    /// The rewritten contents. Keys already in the <c>[Desktop Entry]</c> group are replaced
    /// in place; missing ones are appended to the end of that group, so they cannot land
    /// inside a later <c>[Desktop Action …]</c> group where nothing would read them.
    /// </returns>
    /// <remarks>
    /// Preserving the rest of the file matters: a user or a packager may have added
    /// <c>OnlyShowIn</c>, a delay, or a localised name, and a rewrite that dropped those
    /// would silently change the behaviour they set up.
    /// </remarks>
    public static string SetHidden(string? contents, bool hidden)
    {
        if (string.IsNullOrWhiteSpace(contents))
        {
            return contents ?? string.Empty;
        }

        string hiddenValue = hidden ? "true" : "false";
        string enabledValue = hidden ? "false" : "true";

        List<string> lines = [.. EnumerateLines(contents)];
        bool inGroup = false;
        bool wroteHidden = false;
        bool wroteEnabled = false;
        int groupEnd = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith('['))
            {
                if (inGroup)
                {
                    // The group ends at the line before the next group header.
                    groupEnd = i;
                    inGroup = false;
                }
                else
                {
                    inGroup = trimmed.Equals("[" + GroupName + "]", StringComparison.Ordinal);
                }

                continue;
            }

            if (!inGroup)
            {
                continue;
            }

            int separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            string key = trimmed[..separator].Trim();
            if (key.Equals(HiddenKey, StringComparison.Ordinal))
            {
                lines[i] = HiddenKey + "=" + hiddenValue;
                wroteHidden = true;
            }
            else if (key.Equals(GnomeEnabledKey, StringComparison.Ordinal))
            {
                lines[i] = GnomeEnabledKey + "=" + enabledValue;
                wroteEnabled = true;
            }
        }

        if (inGroup)
        {
            groupEnd = lines.Count;
        }

        if (groupEnd < 0)
        {
            // No [Desktop Entry] group at all. Nothing sensible to edit.
            return string.Join('\n', lines);
        }

        // Insert after the last non-empty line of the group, so an appended key does not end
        // up separated from it by the blank line that usually precedes the next header.
        int insertAt = groupEnd;
        while (insertAt > 0 && lines[insertAt - 1].Trim().Length == 0)
        {
            insertAt--;
        }

        if (!wroteEnabled)
        {
            lines.Insert(insertAt, GnomeEnabledKey + "=" + enabledValue);
        }

        if (!wroteHidden)
        {
            lines.Insert(insertAt, HiddenKey + "=" + hiddenValue);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Quotes an executable path for an <c>Exec</c> value.
    /// </summary>
    /// <param name="executablePath">The path to quote.</param>
    /// <returns>The quoted token, ready to be written after <c>Exec=</c>.</returns>
    /// <remarks>
    /// <para>
    /// Two layers of escaping apply and both are done here, which is the part that is easy to
    /// get wrong. First the <c>Exec</c> layer: the value is always wrapped in double quotes —
    /// so a path containing a space is one argument rather than two — and <c>"</c>,
    /// <c>`</c>, <c>$</c> and <c>\</c> inside it are prefixed with a backslash. Then the
    /// desktop-file value layer, where a backslash is itself an escape character, so every
    /// backslash the first layer produced is doubled. The specification calls this out
    /// explicitly, and it is why a path containing a dollar sign comes back with <c>\\$</c>
    /// rather than <c>\$</c>.
    /// </para>
    /// <para>
    /// A newline or a carriage return cannot appear in a desktop entry value at all; both are
    /// dropped rather than escaped, because a path containing one is not something Altim
    /// should try to register.
    /// </para>
    /// </remarks>
    public static string QuoteExec(string executablePath)
    {
        ArgumentNullException.ThrowIfNull(executablePath);

        var builder = new StringBuilder(executablePath.Length + 8);
        _ = builder.Append('"');

        foreach (char character in executablePath)
        {
            if (character is '\n' or '\r')
            {
                continue;
            }

            if (ExecEscapedCharacters.Contains(character, StringComparison.Ordinal))
            {
                // One backslash for the Exec layer, doubled for the desktop-file value layer.
                _ = builder.Append("\\\\");
            }

            _ = builder.Append(character);
        }

        return builder.Append('"').ToString();
    }

    /// <summary>
    /// Escapes a plain string value for a desktop entry.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The value with backslashes, newlines and tabs escaped.</returns>
    public static string EscapeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateLines(string contents)
    {
        // Split on \n and drop a trailing \r, so a file written on another platform round
        // trips without gaining blank lines.
        foreach (string line in contents.Split('\n'))
        {
            yield return line.EndsWith('\r') ? line[..^1] : line;
        }
    }
}
