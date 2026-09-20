namespace Altim.Platform.Tests;

/// <summary>
/// A throwaway directory, for the two platform seams that take a directory and read it back.
/// </summary>
/// <remarks>
/// The tree is always built here at run time rather than committed. A procfs fixture is a
/// directory of executable names, and an autostart fixture is a desktop entry naming a path on
/// somebody's machine; neither is something a public repository should carry a real copy of.
/// The root is not created until something is written into it, so a test can assert that a
/// call wrote nothing at all.
/// </remarks>
internal sealed class TempTree : IDisposable
{
    public TempTree() =>
        Root = Path.Combine(Path.GetTempPath(), "altim-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The directory, which may not exist yet.</summary>
    public string Root { get; }

    /// <summary>Writes a file with LF endings, creating the directories above it.</summary>
    /// <param name="relativePath">The path inside the tree, with forward slashes.</param>
    /// <param name="content">The file's text.</param>
    /// <returns>The full path written.</returns>
    public string Write(string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(content);

        string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    /// <summary>Creates a directory inside the tree and leaves it empty.</summary>
    /// <param name="relativePath">The path inside the tree, with forward slashes.</param>
    /// <returns>The full path created.</returns>
    public string MakeDirectory(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp directory that will not delete is not a test failure.
        }
    }
}
