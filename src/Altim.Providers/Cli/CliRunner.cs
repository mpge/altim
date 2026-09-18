using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Altim.Providers.Cli;

/// <summary>
/// The real <see cref="ICliRunner"/>.
/// </summary>
/// <remarks>
/// <para>
/// Six things are non-negotiable here, and each of them came from a way this goes wrong:
/// </para>
/// <list type="bullet">
/// <item>
/// No console window. A tray utility that flashes a console on every poll is unusable, so
/// shell execution is off and window creation is suppressed.
/// </item>
/// <item>
/// Standard input is redirected and closed immediately. These CLIs block waiting on stdin
/// when they inherit a handle they can read, and a background poll that blocks forever is
/// worse than one that fails.
/// </item>
/// <item>
/// A hard timeout, enforced by killing the whole process tree. Some of these commands
/// spawn helpers, and killing only the parent leaves those running.
/// </item>
/// <item>
/// Both output pipes are drained to end-of-stream even after the capture cap is reached.
/// A pipe nobody reads fills at about 64 KB and blocks the child's next write, which a
/// caller sees as a timeout on a command that was working perfectly.
/// </item>
/// <item>
/// A missing binary is an outcome, not an exception. "Not installed" is the normal state
/// for a provider the user does not use. A binary that was found and then would not start
/// is a <see cref="CliRunOutcome.Failed"/>, because those two mean different things.
/// </item>
/// <item>
/// Every child joins a job object that dies with Altim. The timeout covers the command that
/// hangs; this covers the one that is working normally when the user quits, which used to
/// go on running for a second or two after the tray icon had gone. See
/// <see cref="ChildProcessJob"/>.
/// </item>
/// </list>
/// </remarks>
public sealed class CliRunner : ICliRunner
{
    /// <summary>
    /// The most standard output this runner will hold. A CLI answer that needs more than
    /// this is not an answer Altim knows how to read, and buffering it would only cost
    /// memory in a process whose whole idle budget is 55MB of private working set.
    /// </summary>
    public const int MaxCapturedOutputBytes = 512 * 1024;

    /// <summary>The folder name created under local application data to run provider CLIs in.</summary>
    private const string WorkingDirectoryName = "cli";

    private static readonly Win32ErrorCode[] MissingFileErrors =
    [
        Win32ErrorCode.FileNotFound,
        Win32ErrorCode.PathNotFound,
    ];

    private readonly int _capturedOutputLimitBytes;

    /// <summary>
    /// Creates a runner.
    /// </summary>
    /// <param name="capturedOutputLimitBytes">
    /// How much standard output to keep, defaulting to <see cref="MaxCapturedOutputBytes"/>.
    /// Output past the limit is read and discarded rather than left in the pipe.
    /// </param>
    public CliRunner(int capturedOutputLimitBytes = MaxCapturedOutputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capturedOutputLimitBytes);
        _capturedOutputLimitBytes = capturedOutputLimitBytes;
    }

    private enum Win32ErrorCode
    {
        FileNotFound = 2,
        PathNotFound = 3,
    }

    /// <summary>
    /// The directory provider CLIs are started in.
    /// </summary>
    /// <returns>
    /// A stable per-user directory under local application data, falling back to the
    /// application's own directory.
    /// </returns>
    /// <remarks>
    /// A provider CLI inherits the working directory otherwise, and that directory is
    /// whatever the user last had open, which would put a real project path into the CLI's
    /// own recent-project list. The temp folder is not the answer either: Claude Code
    /// registers the directory it runs in as a project, so pointing at the temp folder puts
    /// a temp path in the user's project list. This is a fixed, boring directory that Altim
    /// owns, so at worst the user sees one entry that is obviously Altim's.
    /// </remarks>
    public static string NeutralWorkingDirectory()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(root))
            {
                string directory = Path.Combine(root, "Altim", WorkingDirectoryName);
                _ = Directory.CreateDirectory(directory);
                return directory;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            // Fall through to the application directory, which always exists.
        }

        return AppContext.BaseDirectory;
    }

    /// <inheritdoc />
    public bool Exists(string command) => ExecutableResolver.TryResolve(command, out _);

    /// <inheritdoc />
    public async Task<CliRunResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (!ExecutableResolver.TryResolve(command, out string? executable))
        {
            return CliRunResult.NotDetected;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = NeutralWorkingDirectory(),
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return CliRunResult.Failed;
            }

            // Before anything is read from it, so the window in which a child could outlive
            // Altim is a few microseconds rather than the length of the command.
            ChildProcessJob.Adopt(process);
        }
        catch (Win32Exception ex)
        {
            // The resolver found a file a moment ago. If it has since gone the provider is
            // genuinely not installed; anything else — a bad image format, a denied
            // execution — is a fault, and reporting it as "not installed" would tell the
            // user to install software they already have.
            return Array.IndexOf(MissingFileErrors, (Win32ErrorCode)ex.NativeErrorCode) >= 0
                ? CliRunResult.NotDetected
                : CliRunResult.Failed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return CliRunResult.Failed;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            // Close stdin at once: nothing is being sent, and an open handle is what these
            // CLIs block on.
            process.StandardInput.Close();

            Task<string> stdout = ReadCappedAsync(process.StandardOutput, _capturedOutputLimitBytes, timeoutSource.Token);
            Task stderr = DrainAsync(process.StandardError, timeoutSource.Token);

            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);

            return new CliRunResult(CliRunOutcome.Completed, process.ExitCode, output);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            return ct.IsCancellationRequested ? CliRunResult.Failed : CliRunResult.TimedOut;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            KillTree(process);
            return CliRunResult.Failed;
        }
    }

    /// <summary>
    /// Reads standard output, keeping at most <paramref name="limit"/> characters and
    /// discarding the rest.
    /// </summary>
    /// <remarks>
    /// The loop runs to end-of-stream whatever the limit is. Stopping at the limit would
    /// leave the child blocked on a full pipe, and the caller would then kill it on a
    /// timeout and report a failure for a command that answered correctly and simply said
    /// more than Altim keeps.
    /// </remarks>
    private static async Task<string> ReadCappedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var builder = new StringBuilder();
        char[] buffer = new char[8192];

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            int room = Math.Min(read, limit - builder.Length);
            if (room > 0)
            {
                _ = builder.Append(buffer, 0, room);
            }
        }

        return builder.ToString();
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        // Read and discard. An unread pipe fills and deadlocks the child, but the content
        // itself is diagnostics that may quote configuration, so it is never kept.
        char[] buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) > 0)
        {
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception or AggregateException)
        {
            // Already gone, or the kill was refused. Nothing further to do.
        }
    }
}
