namespace Altim.Platform.MacOS;

/// <summary>
/// Decides whether Altim is running from inside a real <c>.app</c> bundle, which several
/// macOS APIs require and which a <c>dotnet run</c> from a source tree never satisfies.
/// </summary>
/// <remarks>
/// <para>
/// This matters more than it looks. <c>UNUserNotificationCenter.currentNotificationCenter</c>
/// and <c>SMAppService</c> both need a bundle identity, and an unbundled process does not
/// get an error back from them: they raise an Objective-C exception, which a managed
/// <c>try</c>/<c>catch</c> cannot catch and which ends the process. The only safe handling
/// is to <em>not call them</em>, so both services ask this type first and degrade to
/// "unavailable" when the answer is no.
/// </para>
/// <para>
/// The path test below is pure and testable. The services additionally check
/// <c>NSBundle.mainBundle.bundleIdentifier</c> at run time and require both to agree,
/// because a directory that merely looks like a bundle — an executable copied into a
/// <c>Foo.app/Contents/MacOS</c> folder by hand, with no <c>Info.plist</c> — has no bundle
/// identity either.
/// </para>
/// </remarks>
public static class MacOSAppBundle
{
    /// <summary>The path segment every bundled macOS executable sits under.</summary>
    private const string BundleExecutableSegment = "/Contents/MacOS/";

    /// <summary>The extension a bundle directory carries.</summary>
    private const string BundleExtension = ".app";

    /// <summary>
    /// True when a path looks like the executable of a macOS application bundle.
    /// </summary>
    /// <param name="executablePath">
    /// The path to test, normally <see cref="Environment.ProcessPath"/>. Null, empty and
    /// whitespace all answer false.
    /// </param>
    /// <returns>
    /// True only for a path of the shape <c>…/Something.app/Contents/MacOS/Something</c>.
    /// A path that contains <c>Contents/MacOS</c> without an enclosing <c>.app</c>
    /// directory is not a bundle and answers false.
    /// </returns>
    public static bool LooksBundled(string? executablePath) => FindBundleRoot(executablePath) is not null;

    /// <summary>
    /// Returns the <c>.app</c> directory a path sits inside.
    /// </summary>
    /// <param name="executablePath">The executable path to inspect.</param>
    /// <returns>
    /// The bundle directory including its <c>.app</c> extension, or <see langword="null"/>
    /// when the path is not inside one. Separators come back as they went in.
    /// </returns>
    public static string? FindBundleRoot(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        // macOS uses forward slashes, but a path may arrive from a Windows-side test or a
        // configuration file, so both separators are accepted and compared uniformly.
        string normalised = executablePath.Replace('\\', '/');

        int segment = normalised.LastIndexOf(BundleExecutableSegment, StringComparison.Ordinal);
        if (segment <= 0)
        {
            return null;
        }

        string root = normalised[..segment];
        return root.EndsWith(BundleExtension, StringComparison.Ordinal) ? root : null;
    }

    /// <summary>
    /// The <c>Contents/Resources</c> directory of the bundle a path sits inside.
    /// </summary>
    /// <param name="executablePath">The executable path to inspect.</param>
    /// <returns>
    /// The resources directory, or <see langword="null"/> when the path is not inside a
    /// bundle. This is where packaging puts the menu bar assets, and is searched before the
    /// directory beside the executable.
    /// </returns>
    public static string? FindResourcesDirectory(string? executablePath)
    {
        string? root = FindBundleRoot(executablePath);
        return root is null ? null : root + "/Contents/Resources";
    }
}
