using System.Diagnostics.CodeAnalysis;

namespace Altim.Providers.Cli;

/// <summary>
/// Finds a provider CLI on the search path without launching anything.
/// </summary>
/// <remarks>
/// <para>
/// "Not installed" has to be cheap and it has to be a settled answer rather than an
/// error, so presence is decided by looking for the file rather than by starting a
/// process and interpreting the failure.
/// </para>
/// <para>
/// On Windows this matters for a second reason: these CLIs ship as <c>.cmd</c> shims, and
/// <c>CreateProcess</c> with shell execution disabled does not apply <c>PATHEXT</c>. A
/// bare command name would simply not be found. Resolving here produces the full path,
/// including the extension, that the process start actually needs.
/// </para>
/// <para>
/// <b>Order is the whole point on Windows.</b> An npm install of the Codex CLI writes
/// <c>codex</c>, <c>codex.cmd</c> and <c>codex.ps1</c> into the same directory. The
/// extensionless file is a POSIX shell script that <c>CreateProcess</c> rejects with a
/// bad-format error, so a resolver that probed the bare name first would answer with a file
/// that cannot be started, and every caller would report the CLI as missing while it was
/// installed and working. Extensions are therefore tried first, in <c>PATHEXT</c> order,
/// exactly as a shell resolves a command; the bare name is the last resort, for the hosts
/// where it is the only thing there.
/// </para>
/// <para>
/// Only extensions that <c>CreateProcess</c> can actually start are considered:
/// <c>.com</c>, <c>.exe</c>, <c>.bat</c> and <c>.cmd</c>. <c>PATHEXT</c> commonly also
/// lists <c>.ps1</c>, <c>.js</c> and friends, which are run by a shell association rather
/// than by the process launcher. Resolving to one of those would only move the bad-format
/// failure one step later, so they are skipped.
/// </para>
/// </remarks>
public static class ExecutableResolver
{
    /// <summary>
    /// The extensions <see cref="System.Diagnostics.Process"/> can start on Windows with
    /// shell execution disabled, in the order Windows itself prefers them.
    /// </summary>
    private static readonly string[] StartableExtensions = [".com", ".exe", ".bat", ".cmd"];

    /// <summary>
    /// Resolves a command name to a full path.
    /// </summary>
    /// <param name="command">
    /// A bare command name such as <c>claude</c>, or a path. A path is returned as-is when
    /// it exists.
    /// </param>
    /// <param name="fullPath">The resolved full path when the method returns true.</param>
    /// <returns>
    /// True when the command exists. False means the provider is not installed on this
    /// machine, which callers report as
    /// <see cref="Core.Models.ProviderStatus.NotDetected"/> rather than as a failure.
    /// </returns>
    public static bool TryResolve(string command, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        if (command.AsSpan().IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) >= 0)
        {
            return TryExistingFile(command, out fullPath);
        }

        string? pathVariable = SafeGetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return false;
        }

        return TryResolveIn(
            pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            command,
            out fullPath);
    }

    /// <summary>
    /// Resolves a bare command name against an explicit list of directories.
    /// </summary>
    /// <param name="searchDirectories">
    /// The directories to search, in order. Earlier entries win, as they do on <c>PATH</c>.
    /// An entry that cannot be read, or is not a valid path at all, is skipped rather than
    /// failing the resolve.
    /// </param>
    /// <param name="command">A bare command name such as <c>codex</c>.</param>
    /// <param name="fullPath">The resolved full path when the method returns true.</param>
    /// <returns>True when a startable file was found.</returns>
    /// <remarks>
    /// Separated from <see cref="TryResolve(string, out string?)"/> so the Windows
    /// resolution order can be tested against a directory laid out like an npm install,
    /// without a test mutating the process's <c>PATH</c>.
    /// </remarks>
    public static bool TryResolveIn(
        IReadOnlyList<string> searchDirectories,
        string command,
        [NotNullWhen(true)] out string? fullPath)
    {
        ArgumentNullException.ThrowIfNull(searchDirectories);

        fullPath = null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        foreach (string directory in searchDirectories)
        {
            foreach (string candidate in Candidates(directory, command))
            {
                if (TryExistingFile(candidate, out fullPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> Candidates(string directory, string command)
    {
        if (OperatingSystem.IsWindows() && !Path.HasExtension(command))
        {
            foreach (string extension in Extensions())
            {
                string? candidate = SafeCombine(directory, command + extension);
                if (candidate is not null)
                {
                    yield return candidate;
                }
            }
        }

        // Last, not first. On Windows this is the POSIX shell script an npm install leaves
        // behind; elsewhere it is the executable itself.
        string? bare = SafeCombine(directory, command);
        if (bare is not null)
        {
            yield return bare;
        }
    }

    /// <summary>
    /// The extensions to try, in <c>PATHEXT</c> order, filtered to the ones the process
    /// launcher can start. Falls back to the fixed list when <c>PATHEXT</c> is unset or
    /// names nothing startable.
    /// </summary>
    private static IEnumerable<string> Extensions()
    {
        string? pathExt = SafeGetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrEmpty(pathExt))
        {
            return StartableExtensions;
        }

        string[] ordered = pathExt
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static e => e.StartsWith('.') ? e : "." + e)

            // PATHEXT is conventionally upper case. Lower casing keeps the resolved path
            // spelled the way the file on disk is, which matters nowhere functionally and
            // everywhere a path is read by a person.
            .Select(static e => e.ToLowerInvariant())
            .Where(static e => StartableExtensions.Contains(e, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        return ordered.Length > 0 ? ordered : StartableExtensions;
    }

    private static string? SafeCombine(string directory, string fileName)
    {
        try
        {
            return Path.Combine(directory, fileName);
        }
        catch (ArgumentException)
        {
            // A malformed PATH entry. Skip it rather than failing the resolve.
            return null;
        }
    }

    private static bool TryExistingFile(string candidate, [NotNullWhen(true)] out string? fullPath)
    {
        try
        {
            if (File.Exists(candidate))
            {
                fullPath = Path.GetFullPath(candidate);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable directory on the path is not an error, just not a match.
        }

        fullPath = null;
        return false;
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
