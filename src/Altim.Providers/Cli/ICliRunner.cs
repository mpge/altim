namespace Altim.Providers.Cli;

/// <summary>
/// Runs a provider CLI once, non-interactively, and captures what it printed.
/// </summary>
/// <remarks>
/// Abstracted so provider readers can be tested without a CLI installed: the tests for
/// "the binary is missing" and "the call failed" substitute a runner rather than
/// manipulating the machine.
/// </remarks>
public interface ICliRunner
{
    /// <summary>
    /// Runs a command and waits for it to exit.
    /// </summary>
    /// <param name="command">
    /// A bare command name such as <c>claude</c>, or a full path. A name that resolves to
    /// nothing yields <see cref="CliRunOutcome.NotDetected"/>.
    /// </param>
    /// <param name="arguments">
    /// The arguments, passed one element per argument so nothing is re-parsed by a shell.
    /// </param>
    /// <param name="timeout">
    /// How long to wait before killing the process and its children.
    /// </param>
    /// <param name="ct">Cancels the wait and kills the process.</param>
    /// <returns>
    /// The outcome. This method does not throw for a CLI-level problem: a missing binary,
    /// a timeout and a crash are all reported as outcomes.
    /// </returns>
    Task<CliRunResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// True when the command exists on this machine, decided without starting a process.
    /// </summary>
    /// <param name="command">A bare command name or a full path.</param>
    bool Exists(string command);
}
