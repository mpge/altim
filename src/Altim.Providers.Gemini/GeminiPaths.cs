namespace Altim.Providers.Gemini;

/// <summary>
/// Where Gemini CLI keeps its local state on this machine, and the one part of it Altim
/// is allowed to look at.
/// </summary>
/// <remarks>
/// <para>
/// <b>The home directory holds credentials, and Altim never opens it.</b>
/// <c>oauth_creds.json</c>, <c>google_accounts.json</c>, <c>mcp-oauth-tokens.json</c> and
/// <c>a2a-oauth-tokens.json</c> all sit at the top level of <c>~/.gemini</c>. Nothing here
/// lists that directory, and the only enumeration this class performs is rooted at a
/// <c>chats</c> directory, which is three levels below it. A reader that started at the home
/// and filtered on the way down would be one typo away from opening a credential file;
/// starting below it means there is no path through this code that can reach one.
/// </para>
/// <para>
/// <c>GEMINI_CLI_HOME</c> replaces the user profile, not the <c>.gemini</c> suffix: the CLI
/// reads that variable in place of the home directory and then appends <c>.gemini</c> to it,
/// so the resolved location is <c>$GEMINI_CLI_HOME/.gemini</c>. Verified in the CLI's own
/// <c>packages/core/src/utils/paths.ts</c> and <c>config/storage.ts</c> at v0.60.0.
/// </para>
/// <para>
/// The directory under <c>tmp</c> that holds a project's sessions is named after the
/// project. Current versions name it with a slug of the project folder's own name, earlier
/// ones with a hash of its full path, and each such directory also carries a marker file
/// holding the absolute project path. <b>None of those names, and no part of that marker,
/// is read, returned, persisted or logged.</b> A directory name is a step on the way to a
/// session file and nothing else, exactly as a Claude Code project slug is.
/// </para>
/// <para>
/// macOS and Linux locations come from upstream source rather than local execution, and are
/// unverified.
/// </para>
/// </remarks>
public static class GeminiPaths
{
    /// <summary>The environment variable Gemini CLI reads in place of the user's home.</summary>
    public const string HomeVariable = "GEMINI_CLI_HOME";

    /// <summary>The directory name Gemini CLI keeps its state in, under a home directory.</summary>
    public const string ConfigDirectoryName = ".gemini";

    /// <summary>The per-project working directory under the Gemini home.</summary>
    public const string TempDirectoryName = "tmp";

    /// <summary>The session-transcript directory under a project directory.</summary>
    public const string ChatsDirectoryName = "chats";

    /// <summary>
    /// Resolves the Gemini CLI home directory.
    /// </summary>
    /// <returns>
    /// The directory, or <see langword="null"/> when neither an override nor a user profile
    /// can be determined. The directory is returned whether or not it exists, because the
    /// caller reports "not installed" from its absence and reading somewhere else instead
    /// would be worse.
    /// </returns>
    public static string? ResolveHome()
    {
        string? overridden = SafeGetEnvironmentVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.Combine(overridden, ConfigDirectoryName);
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, ConfigDirectoryName);
    }

    /// <summary>The per-project working directory under a Gemini home.</summary>
    /// <param name="home">The Gemini home.</param>
    public static string TempDirectory(string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        return Path.Combine(home, TempDirectoryName);
    }

    /// <summary>
    /// The <c>chats</c> directories that exist on this machine, one per project Gemini CLI
    /// has been run in.
    /// </summary>
    /// <param name="home">The Gemini home.</param>
    /// <param name="maxProjects">The most project directories to look inside.</param>
    /// <returns>
    /// The directories that exist, or an empty list when the home holds none. Never the home
    /// itself and never a project directory: only the <c>chats</c> directory inside one,
    /// which is the only place session transcripts are written.
    /// </returns>
    public static IReadOnlyList<string> FindChatDirectories(string home, int maxProjects)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProjects);

        var found = new List<string>();

        try
        {
            int examined = 0;
            foreach (string project in Directory.EnumerateDirectories(TempDirectory(home)))
            {
                if (++examined > maxProjects)
                {
                    break;
                }

                string chats = Path.Combine(project, ChatsDirectoryName);
                if (Directory.Exists(chats))
                {
                    found.Add(chats);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A missing or unreadable temp directory means no sessions, not a failure.
            return found;
        }

        return found;
    }

    /// <summary>
    /// True when a session file path is a subagent transcript.
    /// </summary>
    /// <param name="chatsDirectory">The <c>chats</c> directory the file was found under.</param>
    /// <param name="path">The session file.</param>
    /// <remarks>
    /// A main session is written directly into <c>chats</c>; a subagent's is written into a
    /// directory named after its parent session, one level further down. Subagent files are
    /// read on the same terms as main ones, because on the other transcript-based provider
    /// they carried most of the token volume and a reader that skipped them under-reported
    /// by most of the real usage.
    /// </remarks>
    public static bool IsSubagentSession(string chatsDirectory, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatsDirectory);
        ArgumentException.ThrowIfNullOrEmpty(path);

        string? parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return !string.Equals(
            Path.TrimEndingDirectorySeparator(parent),
            Path.TrimEndingDirectorySeparator(chatsDirectory),
            comparison);
    }

    /// <summary>
    /// True when a file name is one Gemini CLI writes a conversation to.
    /// </summary>
    /// <param name="fileName">A file name with no directory component.</param>
    /// <remarks>
    /// Both extensions are accepted. Current versions write JSON Lines to <c>.jsonl</c>;
    /// versions from before that wrote one JSON document to <c>.json</c>, and those carry no
    /// token counts at all, so such a file contributes its write time and nothing else. The
    /// check is on the exact extension rather than a search pattern, because a Windows
    /// pattern of <c>*.json</c> also matches <c>.jsonl</c> through short-name expansion.
    /// </remarks>
    public static bool IsSessionFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        string extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".jsonl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeGetEnvironmentVariable(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name);
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }
}
