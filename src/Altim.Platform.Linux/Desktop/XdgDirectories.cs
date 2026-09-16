namespace Altim.Platform.Linux.Desktop;

/// <summary>
/// Resolves the XDG base directories Altim writes to on Linux.
/// </summary>
/// <remarks>
/// <para>
/// The rules are the XDG Base Directory specification's, and the one that catches people
/// out is in <see cref="Resolve"/>: an environment variable that is set but is <em>not an
/// absolute path</em> must be ignored, not used. A relative <c>XDG_CONFIG_HOME</c> would
/// otherwise make Altim write its autostart entry somewhere relative to whatever directory
/// it happened to be launched from.
/// </para>
/// <para>
/// Every function here is pure and takes its environment explicitly, so the behaviour is
/// tested rather than assumed.
/// </para>
/// </remarks>
public static class XdgDirectories
{
    /// <summary>The directory name autostart entries live in, under the config home.</summary>
    public const string AutostartDirectoryName = "autostart";

    /// <summary>
    /// Applies the XDG fallback rule to one variable.
    /// </summary>
    /// <param name="environmentValue">The variable's value, or null when it is not set.</param>
    /// <param name="home">The user's home directory, or null when <c>HOME</c> is not set.</param>
    /// <param name="relativeDefault">
    /// The default, relative to <paramref name="home"/>, for example <c>.config</c>.
    /// </param>
    /// <returns>
    /// An absolute directory path, or <see langword="null"/> when neither the variable nor
    /// <c>HOME</c> gives one. A null means Altim has nowhere to write and reports the
    /// feature unavailable instead of guessing at <c>/</c>.
    /// </returns>
    public static string? Resolve(string? environmentValue, string? home, string relativeDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeDefault);

        // "If $XDG_CONFIG_HOME is either not set or empty, a default equal to
        //  $HOME/.config should be used." — and a relative value is invalid, so it is
        //  treated the same as not set rather than resolved against the process directory.
        if (!string.IsNullOrWhiteSpace(environmentValue) && IsAbsolute(environmentValue))
        {
            return TrimTrailingSeparator(environmentValue);
        }

        if (string.IsNullOrWhiteSpace(home) || !IsAbsolute(home))
        {
            return null;
        }

        return TrimTrailingSeparator(home) + "/" + relativeDefault;
    }

    /// <summary>
    /// The autostart directory for a given config home.
    /// </summary>
    /// <param name="configHome">An absolute config home, from <see cref="Resolve"/>.</param>
    /// <returns>The autostart directory, or null when <paramref name="configHome"/> is null.</returns>
    public static string? AutostartDirectory(string? configHome) =>
        configHome is null ? null : configHome + "/" + AutostartDirectoryName;

    /// <summary>
    /// Reads the live environment and returns the autostart directory.
    /// </summary>
    /// <returns>
    /// The directory, or <see langword="null"/> when neither <c>XDG_CONFIG_HOME</c> nor
    /// <c>HOME</c> names an absolute path.
    /// </returns>
    public static string? CurrentAutostartDirectory() =>
        AutostartDirectory(Resolve(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetEnvironmentVariable("HOME"),
            ".config"));

    private static bool IsAbsolute(string path) => path.StartsWith('/');

    private static string TrimTrailingSeparator(string path)
    {
        string trimmed = path.TrimEnd();
        return trimmed.Length > 1 && trimmed.EndsWith('/') ? trimmed.TrimEnd('/') : trimmed;
    }
}
