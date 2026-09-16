namespace Altim.Providers.Claude;

/// <summary>
/// Where Claude Code keeps its local state on this machine.
/// </summary>
/// <remarks>
/// <para>
/// <c>CLAUDE_CONFIG_DIR</c> may list several roots separated by the platform's path
/// separator, so it is treated as a list rather than a single directory. The default root
/// and <c>~/.config/claude</c> are both checked, because installations differ.
/// </para>
/// <para>
/// A project slug is the working directory with non-alphanumerics replaced by hyphens.
/// <b>Altim never decodes one.</b> Slugs are directory names on the way to a transcript and
/// nothing more: decoding one would reconstruct the user's working directory, which is
/// exactly the sort of thing this app has no business knowing.
/// </para>
/// <para>
/// macOS and Linux behaviour is unverified — no such host was available.
/// </para>
/// </remarks>
public static class ClaudePaths
{
    /// <summary>The environment variable that relocates, or multiplies, the config root.</summary>
    public const string ConfigDirectoryVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>The directory transcripts live under, relative to a config root.</summary>
    public const string ProjectsDirectoryName = "projects";

    /// <summary>The directory subagent transcripts live in, relative to a session directory.</summary>
    public const string SubagentsDirectoryName = "subagents";

    /// <summary>
    /// The state file Altim's status-line helper writes, relative to a config root.
    /// </summary>
    /// <remarks>
    /// The helper must return in well under 100 milliseconds: Claude Code debounces status
    /// line updates at 300 milliseconds and cancels an in-flight script when a newer update
    /// arrives. Writing one small file and exiting is the whole design.
    /// </remarks>
    public const string StatusLineStateFileName = "altim-statusline.json";

    /// <summary>
    /// Every config root that exists on this machine, most specific first.
    /// </summary>
    /// <returns>
    /// The roots, or an empty list when Claude Code is not installed. An override that
    /// names a directory which does not exist is still returned, because the user pointed
    /// at it and reading somewhere else instead would be worse.
    /// </returns>
    public static IReadOnlyList<string> ResolveConfigRoots()
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        string? overridden = Environment.GetEnvironmentVariable(ConfigDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            foreach (string entry in overridden.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(entry))
                {
                    roots.Add(entry);
                }
            }

            return roots;
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
        {
            return roots;
        }

        foreach (string candidate in new[] { Path.Combine(profile, ".claude"), Path.Combine(profile, ".config", "claude") })
        {
            if (seen.Add(candidate) && Directory.Exists(candidate))
            {
                roots.Add(candidate);
            }
        }

        return roots;
    }

    /// <summary>The transcript directory under a config root.</summary>
    /// <param name="configRoot">A config root.</param>
    public static string ProjectsDirectory(string configRoot) => Path.Combine(configRoot, ProjectsDirectoryName);

    /// <summary>The settings file under a config root.</summary>
    /// <param name="configRoot">A config root.</param>
    public static string SettingsFile(string configRoot) => Path.Combine(configRoot, "settings.json");

    /// <summary>The status-line state file under a config root.</summary>
    /// <param name="configRoot">A config root.</param>
    public static string StatusLineStateFile(string configRoot) => Path.Combine(configRoot, StatusLineStateFileName);

    /// <summary>
    /// True when a transcript path sits in a subagents directory.
    /// </summary>
    /// <param name="path">A transcript path.</param>
    /// <remarks>
    /// Subagent transcripts are not an optional extra. Including them took one measured
    /// session from 24.8 million to 125.1 million cache-read tokens, and the store held
    /// 2,056 of them against 22 main transcripts. A reader that skipped this directory
    /// would under-report by most of the real usage.
    /// </remarks>
    public static bool IsSubagentTranscript(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string? parent = Path.GetFileName(Path.GetDirectoryName(path.AsSpan()).ToString());
        return string.Equals(parent, SubagentsDirectoryName, StringComparison.OrdinalIgnoreCase);
    }
}
