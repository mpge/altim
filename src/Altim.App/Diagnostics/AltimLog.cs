using System.Globalization;
using System.Text;
using Altim.Core.Diagnostics;

namespace Altim.App.Diagnostics;

/// <summary>
/// A one-file log for a process with no console and no window to put a message in.
/// </summary>
/// <remarks>
/// <para>
/// Altim degrades rather than crashing, which means the interesting events — a provider
/// that is not installed, a notification platform that refused registration, a database
/// that could not be opened — never reach the user as an exception. They reach the tray
/// menu as one line each, and they reach this file with enough detail to act on.
/// </para>
/// <para>
/// <b>Nothing written here comes from outside Altim.</b> Every line is one of Altim's own
/// sentences, and an exception contributes its <em>type</em> and nothing else. That last
/// part was a defect rather than a decision: the line used to carry
/// <c>Exception.Message</c> as well, and those messages are written by the framework and by
/// other people's libraries. An <see cref="IOException"/> names the file it failed on, a
/// watcher overflow names the directory being watched, and a provider failure carries
/// whichever path its reader was inside — so a promise PRIVACY.md makes about the database
/// was quietly not kept by the log file beside it, which is the file people attach to bug
/// reports. See <see cref="ExceptionSummary"/>.
/// </para>
/// </remarks>
internal static class AltimLog
{
    /// <summary>Past this the file is started again, so it cannot grow without bound.</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly Lock Gate = new();
    private static string? _path;

    /// <summary>The file being written, or null until <see cref="Initialize"/> has run.</summary>
    public static string? Path
    {
        get
        {
            lock (Gate)
            {
                return _path;
            }
        }
    }

    /// <summary>
    /// Points the log at a directory, creating it if it is missing. A directory that
    /// cannot be created leaves the log inert rather than failing start-up.
    /// </summary>
    /// <param name="directory">The directory to write into.</param>
    public static void Initialize(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        try
        {
            _ = Directory.CreateDirectory(directory);
            string file = System.IO.Path.Combine(directory, "altim.log");

            var info = new FileInfo(file);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Delete(file);
            }

            lock (Gate)
            {
                _path = file;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A machine that will not let Altim write a log is still a machine Altim runs on.
        }
    }

    /// <summary>Writes one line.</summary>
    /// <param name="category">The area the line is about, for example "tray".</param>
    /// <param name="message">The line. Never provider data.</param>
    public static void Write(string category, string message)
    {
        string? file = Path;
        if (file is null)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append("  [").Append(category).Append("] ")
            .Append(message)
            .Append(Environment.NewLine)
            .ToString();

        lock (Gate)
        {
            try
            {
                File.AppendAllText(file, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging is never worth an exception on the path that was doing real work.
            }
        }
    }

    /// <summary>Writes one line with an exception's type appended.</summary>
    /// <param name="category">The area the line is about.</param>
    /// <param name="message">The line. Always Altim's own sentence.</param>
    /// <param name="error">
    /// The exception to describe. Its type reaches the file; its message never does, because
    /// an exception message is written by whatever threw it and routinely names a path.
    /// </param>
    public static void Write(string category, string message, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Write(category, string.Create(CultureInfo.InvariantCulture, $"{message}: {ExceptionSummary.Describe(error)}"));
    }

    /// <summary>Writes a millisecond measurement, which is how the budgets are checked.</summary>
    /// <param name="what">What was measured.</param>
    /// <param name="elapsed">How long it took.</param>
    public static void Timing(string what, TimeSpan elapsed) =>
        Write("timing", string.Create(CultureInfo.InvariantCulture, $"{what} {elapsed.TotalMilliseconds:F1}ms"));
}
