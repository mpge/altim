using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Altim.Providers.Claude.StatusLine;

/// <summary>
/// The status-line command itself: the branch of Altim's own executable that Claude Code
/// runs, and that <see cref="StatusLineInstaller"/> registers.
/// </summary>
/// <remarks>
/// <para>
/// Claude Code writes its status-line payload to this process's standard input and renders
/// whatever comes back on standard output. So the helper has exactly two jobs: keep the
/// numbers Altim needs, and give the user a line worth looking at.
/// </para>
/// <para>
/// <b>It must return in well under 100 milliseconds.</b> Claude Code debounces status-line
/// updates at 300 milliseconds and cancels an in-flight command when a newer update
/// arrives, so a helper that did anything else would simply never be seen. Parse, write one
/// small file, print one line, exit. No database, no provider, no Avalonia; the branch runs
/// before the entry point has touched any of them.
/// </para>
/// <para>
/// <b>Only numbers are written down.</b> The payload carries the working directory, the
/// project directory, the transcript path, a session id and the model. None of them reaches
/// the state file or the printed line, and the reason is structural rather than careful:
/// <see cref="WriteState"/> can only write numbers and instants, so there is no path through
/// it that a string could take. That is what <c>PRIVACY.md</c> promises and what
/// <see cref="ClaudeStatusLineReader"/> already documents the file as being — the documented
/// payload with everything non-numeric dropped.
/// </para>
/// <para>
/// The file is replaced rather than rewritten. <see cref="ClaudeStatusLineReader"/> is built
/// to survive a reader landing mid-rewrite and treats the empty read as transient, but
/// writing beside the target and swapping it in means the case cannot arise: a reader either
/// sees the whole previous file or the whole new one. See <see cref="WriteState"/> for why
/// the swap is a replace rather than a move.
/// </para>
/// </remarks>
public static class ClaudeStatusLineHelper
{
    /// <summary>
    /// The argument that selects this branch, and the marker
    /// <see cref="StatusLineInstaller.Revert"/> recognises its own command by.
    /// </summary>
    public const string Argument = StatusLineInstaller.CommandMarker;

    /// <summary>
    /// What is printed when the payload carried no figure at all. Claude Code renders the
    /// command's output verbatim, so returning nothing would leave the user with a blank
    /// status bar and no way to tell a quiet session from a broken helper.
    /// </summary>
    public const string NothingReportedText = "altim: no limits reported yet";

    /// <summary>
    /// The largest payload that is read. The documented one is a couple of kilobytes;
    /// anything past this is not the payload and is not parsed.
    /// </summary>
    private const int MaxPayloadBytes = 256 * 1024;

    private const string Separator = " | ";

    /// <summary>
    /// What may sit in front of the payload and is not part of it: a byte-order mark, a
    /// zero-width space, and ordinary leading whitespace.
    /// </summary>
    private static readonly char[] LeadingNoise = ['\uFEFF', '\u200B', ' ', '\t', '\r', '\n'];

    /// <summary>
    /// True when this process was started as the status-line command.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <remarks>
    /// Matched by <em>containment</em>, the same way revert matches, so the two agree about
    /// which command lines are Altim's. A user who wrapped the command in a shell or added a
    /// flag of their own is still recognised here and can still uninstall cleanly.
    /// </remarks>
    public static bool IsHelperInvocation(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return false;
        }

        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is { } argument && argument.Contains(Argument, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs the helper: read the payload, write the state file, print the status line.
    /// </summary>
    /// <param name="input">Standard input, which Claude Code writes the payload to.</param>
    /// <param name="output">Standard output, which Claude Code renders.</param>
    /// <param name="timeProvider">The clock, or null for the system one.</param>
    /// <param name="configRootOverride">
    /// The config root to write under, or null for the first one
    /// <see cref="ClaudePaths.ResolveConfigRoots"/> finds. Tests pass their own directory;
    /// nothing else does.
    /// </param>
    /// <returns>
    /// Zero, always. A payload that could not be parsed and a state file that could not be
    /// written are both reported by the printed line and by the metric staying unavailable,
    /// never by a failure exit code: Claude Code runs this on a timer, and a helper that
    /// exits non-zero on a transient disk error would put an error in the user's status bar
    /// for as long as it lasted.
    /// </returns>
    public static int Run(
        Stream input,
        TextWriter output,
        TimeProvider? timeProvider = null,
        string? configRootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        ClaudeStatusLineState? state = ReadPayload(input, now);

        if (state is not null && ResolveStateFile(configRootOverride) is { } path)
        {
            TryWrite(path, state);
        }

        output.Write(Render(state, now));
        return 0;
    }

    /// <summary>
    /// The line Claude Code shows, built from the payload it just handed over.
    /// </summary>
    /// <param name="state">The parsed payload, or null when there was not one.</param>
    /// <param name="now">The instant the reset countdowns are measured from.</param>
    /// <returns>
    /// Something short and never empty, in ASCII, of the shape
    /// <c>5h 53% (3h12m) | 7d 85% (2d4h) | ctx 21% | $1.23</c>. Each part appears only when
    /// the payload reported it, so a session before its first response prints
    /// <see cref="NothingReportedText"/> rather than a row of zeroes.
    /// </returns>
    /// <remarks>
    /// Time to reset rather than a clock time: it is the figure the user is actually asking
    /// for, and it needs no time zone to read. A window whose reset has already passed
    /// prints its percentage without a countdown rather than a negative one.
    /// </remarks>
    public static string Render(ClaudeStatusLineState? state, DateTimeOffset now)
    {
        if (state is null)
        {
            return NothingReportedText;
        }

        var line = new StringBuilder(64);

        AppendWindow(line, "5h", state.FiveHourUsedPercent, state.FiveHourResetsAt, now);
        AppendWindow(line, "7d", state.SevenDayUsedPercent, state.SevenDayResetsAt, now);
        AppendWindow(line, "spend", state.SpendLimitUsedPercent, state.SpendLimitResetsAt, now);

        if (state.ContextUsedPercent is { } context)
        {
            Append(line, "ctx " + FormatPercent(context));
        }

        if (state.SessionCostUsd is { } cost)
        {
            Append(line, "$" + cost.ToString("0.00", CultureInfo.InvariantCulture));
        }

        return line.Length == 0 ? NothingReportedText : line.ToString();
    }

    /// <summary>
    /// Writes the state file beside the target and swaps it in, so no reader can see a
    /// partial one.
    /// </summary>
    /// <param name="path">The state file.</param>
    /// <param name="state">What to record.</param>
    /// <remarks>
    /// <para>
    /// The temporary name carries the process id, because two Claude Code sessions run the
    /// helper at the same time and a shared temporary name would let one truncate the file
    /// the other was still writing.
    /// </para>
    /// <para>
    /// <b>The swap is <see cref="File.Replace(string, string, string?)"/>, not
    /// <c>File.Move(overwrite: true)</c>, and the difference is not cosmetic.</b> Measured on
    /// Windows 11 26200: with the state file open by a reader using exactly the sharing
    /// <see cref="ClaudeStatusLineReader"/> asks for, the move fails with
    /// <see cref="UnauthorizedAccessException"/> and leaves the stale file and the temporary
    /// one both on disk, while the replace succeeds, leaves the reader's handle reading the
    /// file it opened, and puts the new document in place. The rename that
    /// <c>MOVEFILE_REPLACE_EXISTING</c> performs is refused while the destination has an open
    /// handle; <c>ReplaceFile</c> is the call built for this. Altim's own provider reads this
    /// file every refresh tick, so the move would have dropped whichever write it collided
    /// with.
    /// </para>
    /// <para>
    /// The move is still the path for a state file that does not exist yet, which is what
    /// <c>ReplaceFile</c> refuses, and the fallback for a filesystem that will not replace.
    /// </para>
    /// </remarks>
    public static void WriteState(string path, ClaudeStatusLineState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(state);

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (directory.Length > 0)
        {
            _ = Directory.CreateDirectory(directory);
        }

        string temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";

        try
        {
            File.WriteAllBytes(temporary, Serialize(state));
            SwapIntoPlace(temporary, path);
        }
        catch
        {
            Discard(temporary);
            throw;
        }
    }

    /// <summary>
    /// The state file's bytes: the numbers this state carries and nothing else.
    /// </summary>
    /// <param name="state">The parsed payload.</param>
    /// <returns>UTF-8 JSON the reader parses back into the same numbers.</returns>
    /// <remarks>
    /// <para>
    /// The property names are the payload's own, so the reader has one parser for the
    /// payload and for this file.
    /// </para>
    /// <para>
    /// A figure the payload did not carry is absent here too. It is never written as zero:
    /// a window Claude Code dropped because its reset passed means "no data", and a zero
    /// would be read downstream as "nothing used". The model and the session id the reader
    /// is able to parse are deliberately not written, because Altim has no use for either
    /// and a file it does not write cannot leak one.
    /// </para>
    /// </remarks>
    public static byte[] Serialize(ClaudeStatusLineState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var buffer = new MemoryStream(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (state.WrittenAt is { } writtenAt)
            {
                writer.WriteNumber("written_at", writtenAt.ToUnixTimeSeconds());
            }

            if (HasWindow(state.FiveHourUsedPercent, state.FiveHourResetsAt)
                || HasWindow(state.SevenDayUsedPercent, state.SevenDayResetsAt)
                || HasWindow(state.SpendLimitUsedPercent, state.SpendLimitResetsAt))
            {
                writer.WriteStartObject("rate_limits");
                WriteWindow(writer, "five_hour", state.FiveHourUsedPercent, state.FiveHourResetsAt);
                WriteWindow(writer, "seven_day", state.SevenDayUsedPercent, state.SevenDayResetsAt);
                WriteWindow(writer, "spend_limit", state.SpendLimitUsedPercent, state.SpendLimitResetsAt);
                writer.WriteEndObject();
            }

            if (state.SessionCostUsd is { } cost)
            {
                writer.WriteStartObject("cost");
                writer.WriteNumber("total_cost_usd", cost);
                writer.WriteEndObject();
            }

            if (state.ContextUsedTokens is not null || state.ContextMaxTokens is not null)
            {
                writer.WriteStartObject("context_window");
                WriteCount(writer, "used_tokens", state.ContextUsedTokens);
                WriteCount(writer, "max_tokens", state.ContextMaxTokens);
                writer.WriteEndObject();
            }

            if (state.PromptCacheReadTokens is not null || state.PromptCacheCreationTokens is not null)
            {
                writer.WriteStartObject("prompt_cache");
                WriteCount(writer, "cache_read_input_tokens", state.PromptCacheReadTokens);
                WriteCount(writer, "cache_creation_input_tokens", state.PromptCacheCreationTokens);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The state file this helper writes, or null when no config root can be found.
    /// </summary>
    /// <param name="configRootOverride">The root to use, or null to resolve one.</param>
    public static string? ResolveStateFile(string? configRootOverride)
    {
        if (configRootOverride is not null)
        {
            return ClaudePaths.StatusLineStateFile(configRootOverride);
        }

        IReadOnlyList<string> roots = ClaudePaths.ResolveConfigRoots();
        return roots.Count > 0 ? ClaudePaths.StatusLineStateFile(roots[0]) : null;
    }

    private static ClaudeStatusLineState? ReadPayload(Stream input, DateTimeOffset now)
    {
        try
        {
            using var buffer = new MemoryStream(4096);
            input.CopyTo(buffer);

            if (buffer.Length is 0 or > MaxPayloadBytes)
            {
                return null;
            }

            // Trimmed rather than parsed as-is because a byte-order mark is a character as
            // far as the JSON parser is concerned, and the whole payload is then discarded
            // over one invisible byte at the front. Observed while driving the helper from a
            // PowerShell host, which writes one.
            string payload = Encoding.UTF8
                .GetString(buffer.GetBuffer(), 0, (int)buffer.Length)
                .TrimStart(LeadingNoise);

            return ClaudeStatusLineReader.Parse(payload, now);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            return null;
        }
    }

    private static void TryWrite(string path, ClaudeStatusLineState state)
    {
        try
        {
            WriteState(path, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // The status line is still printed. A disk that cannot be written to this tick
            // leaves the metric unavailable, which is the correct answer, and there is
            // nowhere to report it that is not the user's status bar.
        }
    }

    private static void SwapIntoPlace(string temporary, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(temporary, path, overwrite: true);
            return;
        }

        try
        {
            File.Replace(temporary, path, destinationBackupFileName: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The state file went away between the check and the replace, or this
            // filesystem does not support one. Either way the rename is still atomic.
            File.Move(temporary, path, overwrite: true);
        }
    }

    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Nothing to do about a temporary file that will not go away.
        }
    }

    private static bool HasWindow(double? percent, DateTimeOffset? resetsAt) =>
        percent is not null || resetsAt is not null;

    private static void WriteWindow(Utf8JsonWriter writer, string name, double? percent, DateTimeOffset? resetsAt)
    {
        if (!HasWindow(percent, resetsAt))
        {
            return;
        }

        writer.WriteStartObject(name);

        if (percent is { } used)
        {
            writer.WriteNumber("used_percentage", used);
        }

        if (resetsAt is { } instant)
        {
            writer.WriteNumber("resets_at", instant.ToUnixTimeSeconds());
        }

        writer.WriteEndObject();
    }

    private static void WriteCount(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } count)
        {
            writer.WriteNumber(name, count);
        }
    }

    private static void AppendWindow(StringBuilder line, string label, double? percent, DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (percent is not { } used)
        {
            return;
        }

        string part = label + " " + FormatPercent(used);

        if (resetsAt is { } instant && instant > now)
        {
            part += " (" + FormatRemaining(instant - now) + ")";
        }

        Append(line, part);
    }

    private static void Append(StringBuilder line, string part)
    {
        if (line.Length > 0)
        {
            _ = line.Append(Separator);
        }

        _ = line.Append(part);
    }

    private static string FormatPercent(double percent) =>
        ((int)Math.Round(percent, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%";

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalMinutes < 1d)
        {
            return "<1m";
        }

        if (remaining.TotalHours < 1d)
        {
            return Number(remaining.Minutes) + "m";
        }

        if (remaining.TotalDays < 1d)
        {
            return Number(remaining.Hours) + "h" + Number(remaining.Minutes) + "m";
        }

        return Number((int)remaining.TotalDays) + "d" + Number(remaining.Hours) + "h";
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
