using System.Globalization;

namespace Altim.Providers.Codex;

/// <summary>
/// Where Codex keeps its local state on this machine.
/// </summary>
/// <remarks>
/// <para>
/// <c>CODEX_HOME</c> overrides the default and has no XDG fallback: when it is set, that
/// directory is the answer whether or not it exists, because silently reading a different
/// directory than the one the user pointed at would be worse than reporting nothing.
/// </para>
/// <para>
/// State database filenames carry a schema version — <c>state_5.sqlite</c> today, and the
/// number has changed before — so the file is discovered rather than hard-coded. Only the
/// top level of the Codex home is listed to find it; nothing here walks the session tree.
/// </para>
/// <para>
/// macOS and Linux locations come from upstream source rather than local execution, and
/// are unverified.
/// </para>
/// </remarks>
public static class CodexPaths
{
    /// <summary>The environment variable that relocates the Codex home.</summary>
    public const string HomeVariable = "CODEX_HOME";

    private const string StatePrefix = "state_";
    private const string StateSuffix = ".sqlite";

    /// <summary>
    /// Resolves the Codex home directory.
    /// </summary>
    /// <returns>
    /// The directory, or <see langword="null"/> when neither an override nor a user profile
    /// can be determined.
    /// </returns>
    public static string? ResolveHome()
    {
        string? overridden = Environment.GetEnvironmentVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, ".codex");
    }

    /// <summary>The directory live sessions are written to.</summary>
    /// <param name="home">The Codex home.</param>
    public static string SessionsDirectory(string home) => Path.Combine(home, "sessions");

    /// <summary>The directory archived sessions move to.</summary>
    /// <param name="home">The Codex home.</param>
    public static string ArchivedSessionsDirectory(string home) => Path.Combine(home, "archived_sessions");

    /// <summary>
    /// Finds the highest-versioned state database in the Codex home.
    /// </summary>
    /// <param name="home">The Codex home.</param>
    /// <returns>
    /// The path, or <see langword="null"/> when the directory is missing or holds no
    /// state database. A missing database is not an error: it means the fallback picks
    /// candidate rollouts by modification time instead.
    /// </returns>
    public static string? FindStateDatabase(string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);

        string? best = null;
        long bestVersion = long.MinValue;

        try
        {
            foreach (string path in Directory.EnumerateFiles(home, StatePrefix + "*" + StateSuffix, SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                ReadOnlySpan<char> middle = name.AsSpan(StatePrefix.Length, name.Length - StatePrefix.Length - StateSuffix.Length);
                long version = long.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ? parsed : -1;

                if (version > bestVersion)
                {
                    bestVersion = version;
                    best = path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return best;
    }

    /// <summary>
    /// Lists recent rollout files without walking the session tree.
    /// </summary>
    /// <param name="home">The Codex home.</param>
    /// <param name="maxFiles">The most files to return.</param>
    /// <returns>
    /// Newest first, across both the live and archived stores, de-duplicated by full path.
    /// </returns>
    /// <remarks>
    /// This is the fallback for when the state database cannot be read, and it is
    /// deliberately shallow. Sessions live at <c>sessions/YYYY/MM/DD/</c>, so the walk
    /// descends by directory name — highest year, then month, then day — and stops as soon
    /// as it has enough files. A store with 2,518 files across seven years costs a handful
    /// of directory listings, not a tree walk.
    /// </remarks>
    public static IReadOnlyList<string> FindRecentRollouts(string home, int maxFiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFiles);

        var found = new List<string>(maxFiles);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (string root in new[] { SessionsDirectory(home), ArchivedSessionsDirectory(home) })
        {
            CollectNewest(root, depth: 3, maxFiles, found, seen);
        }

        return found;
    }

    private static void CollectNewest(string directory, int depth, int maxFiles, List<string> found, HashSet<string> seen)
    {
        if (found.Count >= maxFiles)
        {
            return;
        }

        if (depth <= 0)
        {
            foreach (string file in EnumerateNewestFiles(directory, maxFiles - found.Count))
            {
                if (seen.Add(file))
                {
                    found.Add(file);
                    if (found.Count >= maxFiles)
                    {
                        return;
                    }
                }
            }

            return;
        }

        foreach (string child in EnumerateDescendingDirectories(directory))
        {
            CollectNewest(child, depth - 1, maxFiles, found, seen);
            if (found.Count >= maxFiles)
            {
                return;
            }
        }

        // A store that does not use the dated layout still has files at this level.
        foreach (string file in EnumerateNewestFiles(directory, maxFiles - found.Count))
        {
            if (seen.Add(file))
            {
                found.Add(file);
                if (found.Count >= maxFiles)
                {
                    return;
                }
            }
        }
    }

    private static IReadOnlyList<string> EnumerateDescendingDirectories(string directory)
    {
        try
        {
            string[] children = Directory.GetDirectories(directory);
            Array.Sort(children, static (a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));
            return children;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> EnumerateNewestFiles(string directory, int take)
    {
        if (take <= 0)
        {
            return [];
        }

        try
        {
            var files = new List<(string Path, DateTime Written)>();
            foreach (string path in Directory.EnumerateFiles(directory, "rollout-*.jsonl", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    files.Add((path, File.GetLastWriteTimeUtc(path)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Vanished between listing and stat. Skip it.
                }
            }

            files.Sort(static (a, b) => b.Written.CompareTo(a.Written));
            return files.Take(take).Select(static f => f.Path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
