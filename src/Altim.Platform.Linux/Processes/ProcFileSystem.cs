using System.Globalization;

namespace Altim.Platform.Linux.Processes;

/// <summary>
/// The small amount of parsing <c>/proc</c> needs, kept pure so it can be tested off Linux.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only <c>/proc/&lt;pid&gt;/comm</c> is ever opened.</b> <c>/proc/&lt;pid&gt;/cmdline</c>
/// is right next to it and would give an untruncated name, and it is deliberately never
/// read: command lines on a development machine routinely carry API keys and tokens as
/// arguments, and a monitor that opened that file would ingest other people's secrets and
/// then leak them into whatever it wrote next. There is no code path in Altim that opens
/// <c>cmdline</c>, <c>environ</c> or <c>exe</c>.
/// </para>
/// <para>
/// <b>The cost is fifteen characters.</b> The kernel stores <c>comm</c> in a sixteen-byte
/// field including its terminator, so a name longer than fifteen characters comes back
/// truncated. <see cref="MatchesWatchedName"/> handles that by comparing a watched name
/// against its own truncation when — and only when — the value read is exactly at the limit,
/// so <c>claude</c> and <c>codex</c> match exactly and a hypothetical longer name still
/// matches rather than silently never being found.
/// </para>
/// </remarks>
public static class ProcFileSystem
{
    /// <summary>
    /// The longest name the kernel will report, which is <c>TASK_COMM_LEN</c> minus its
    /// terminator.
    /// </summary>
    public const int MaxCommLength = 15;

    /// <summary>The procfs mount point.</summary>
    public const string ProcRoot = "/proc";

    /// <summary>
    /// Parses a <c>/proc</c> entry name as a process id.
    /// </summary>
    /// <param name="directoryName">The entry name, with no directory component.</param>
    /// <param name="processId">The id, when the name was one.</param>
    /// <returns>
    /// True for an all-digit name. <c>/proc</c> also holds <c>self</c>, <c>net</c>,
    /// <c>meminfo</c> and a few dozen other entries, and every one of those is skipped here.
    /// </returns>
    public static bool TryParseProcessId(string? directoryName, out int processId)
    {
        processId = 0;

        if (string.IsNullOrEmpty(directoryName))
        {
            return false;
        }

        foreach (char character in directoryName)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return int.TryParse(directoryName, NumberStyles.None, CultureInfo.InvariantCulture, out processId);
    }

    /// <summary>
    /// Cleans the contents of <c>/proc/&lt;pid&gt;/comm</c>.
    /// </summary>
    /// <param name="raw">The file contents, which carry a trailing newline.</param>
    /// <returns>The executable name, or an empty string when there was nothing to read.</returns>
    public static string NormaliseComm(string? raw) =>
        string.IsNullOrEmpty(raw) ? string.Empty : raw.Trim('\n', '\r', ' ', '\t', '\0');

    /// <summary>
    /// Whether a name read from <c>comm</c> is the process Altim is looking for.
    /// </summary>
    /// <param name="comm">The value from <c>comm</c>, already normalised.</param>
    /// <param name="watchedName">The executable name Altim watches for.</param>
    /// <returns>
    /// True on an exact case-insensitive match, or when <paramref name="comm"/> is exactly
    /// <see cref="MaxCommLength"/> characters long and matches the first
    /// <see cref="MaxCommLength"/> characters of <paramref name="watchedName"/> — the
    /// truncation case. A shorter <paramref name="comm"/> was not truncated, so a prefix
    /// match there would be a false positive and is not accepted.
    /// </returns>
    public static bool MatchesWatchedName(string? comm, string watchedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watchedName);

        if (string.IsNullOrEmpty(comm))
        {
            return false;
        }

        if (comm.Equals(watchedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (comm.Length != MaxCommLength || watchedName.Length <= MaxCommLength)
        {
            return false;
        }

        return comm.Equals(watchedName[..MaxCommLength], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns the modification time of a <c>/proc/&lt;pid&gt;</c> directory into a start time.
    /// </summary>
    /// <param name="modified">The directory's last write time, in UTC.</param>
    /// <returns>
    /// The start time, or <see langword="null"/> when the value is not plausible. The kernel
    /// stamps the directory when the process is created, but a filesystem that cannot report
    /// one answers with the epoch or with <see cref="DateTime.MinValue"/>, and
    /// <see cref="Core.Models.DetectedProcess.StartedAt"/> is documented as null meaning "the
    /// operating system would not say" rather than 1970.
    /// </returns>
    public static DateTimeOffset? NormaliseStartTime(DateTime modified)
    {
        if (modified.Year <= 1970)
        {
            return null;
        }

        DateTime utc = modified.Kind == DateTimeKind.Utc
            ? modified
            : DateTime.SpecifyKind(modified.ToUniversalTime(), DateTimeKind.Utc);

        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
