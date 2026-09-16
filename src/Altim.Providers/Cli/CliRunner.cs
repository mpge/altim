using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Altim.Providers.Cli;

/// <summary>
/// The real <see cref="ICliRunner"/>.
/// </summary>
/// <remarks>
/// <para>
/// Four things are non-negotiable here, and each of them came from a way this goes wrong:
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
/// A missing binary is an outcome, not an exception. "Not installed" is the normal state
/// for a provider the user does not use.
/// </item>
/// </list>
/// </remarks>
public sealed class CliRunner : ICliRunner
{
    /// <summary>
    /// The most standard output this runner will hold. A CLI answer that needs more than
    /// this is not an answer Altim knows how to read, and buffering it would only cost
    /// memory in a process with an 80 MB working-set budget.
    /// </summary>
    public const int MaxCapturedOutputBytes = 512 * 1024;

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

            // A provider CLI inherits the working directory otherwise, and that directory
            // is whatever the user last had open. Anchor it somewhere neutral.
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
        }
        catch (Win32Exception)
        {
            // Resolved a moment ago, gone or not executable now.
            return CliRunResult.NotDetected;
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

            Task<string> stdout = ReadCappedAsync(process.StandardOutput, timeoutSource.Token);
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

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken ct)
    {
        var builder = new StringBuilder();
        char[] buffer = new char[8192];

        while (builder.Length < MaxCapturedOutputBytes)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            int room = Math.Min(read, MaxCapturedOutputBytes - builder.Length);
            _ = builder.Append(buffer, 0, room);
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

    private static string NeutralWorkingDirectory()
    {
        try
        {
            string temp = Path.GetTempPath();
            return Directory.Exists(temp) ? temp : AppContext.BaseDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return AppContext.BaseDirectory;
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
