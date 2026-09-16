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
/// </remarks>
public static class ExecutableResolver
{
    private static readonly string[] WindowsFallbackExtensions = [".exe", ".cmd", ".bat", ".com"];

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

        foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
        string? bare = SafeCombine(directory, command);
        if (bare is not null)
        {
            yield return bare;
        }

        if (!OperatingSystem.IsWindows() || Path.HasExtension(command))
        {
            yield break;
        }

        foreach (string extension in Extensions())
        {
            string? candidate = SafeCombine(directory, command + extension);
            if (candidate is not null)
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> Extensions()
    {
        string? pathExt = SafeGetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrEmpty(pathExt))
        {
            return WindowsFallbackExtensions;
        }

        return pathExt
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static e => e.StartsWith('.') ? e : "." + e);
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
