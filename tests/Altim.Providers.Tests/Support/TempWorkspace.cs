using System.Text;

namespace Altim.Providers.Tests.Support;

/// <summary>
/// A throwaway directory for hand-written fixtures.
/// </summary>
/// <remarks>
/// <b>Every fixture in this suite is synthetic and written here at run time.</b> No real
/// transcript, rollout, settings file or state database is copied into the repository, for
/// the obvious reason: those files contain prompts, source code, command output and working
/// directories, and a test fixture is a file that gets committed, mirrored and searched
/// forever.
/// </remarks>
internal sealed class TempWorkspace : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "altim-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Writes a file, creating its directory, with LF line endings and no byte-order mark.</summary>
    public string Write(string relativePath, string content) =>
        WriteRaw(relativePath, content.ReplaceLineEndings("\n"));

    /// <summary>Writes a file byte for byte, so a fixture can pin its own line endings.</summary>
    public string WriteRaw(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, Utf8NoBom);
        return path;
    }

    /// <summary>Writes JSON Lines, one entry per element, with a trailing newline.</summary>
    public string WriteLines(string relativePath, params string[] lines) =>
        Write(relativePath, string.Join("\n", lines) + "\n");

    /// <summary>
    /// Writes JSON Lines whose last line is cut off mid-token, as a file being appended to
    /// right now looks.
    /// </summary>
    public string WriteLinesWithPartialTail(string relativePath, string[] completeLines, string partialTail) =>
        Write(relativePath, string.Join("\n", completeLines) + "\n" + partialTail);

    /// <summary>Appends to an existing file without rewriting it.</summary>
    public void Append(string path, string content) =>
        File.AppendAllText(path, content.ReplaceLineEndings("\n"), Utf8NoBom);

    public string Path_(params string[] parts) => Path.Combine([Root, .. parts]);

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
