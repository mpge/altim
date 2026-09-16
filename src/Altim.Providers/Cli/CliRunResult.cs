namespace Altim.Providers.Cli;

/// <summary>
/// How a CLI invocation ended.
/// </summary>
public enum CliRunOutcome
{
    /// <summary>The process ran and exited. <see cref="CliRunResult.ExitCode"/> says how.</summary>
    Completed = 0,

    /// <summary>
    /// The executable is not on this machine. A settled answer, not a failure: the
    /// provider is simply not installed.
    /// </summary>
    NotDetected = 1,

    /// <summary>
    /// The process did not exit within its timeout and was killed with its children.
    /// </summary>
    TimedOut = 2,

    /// <summary>
    /// The process could not be started, or the read of its output failed.
    /// </summary>
    Failed = 3,
}

/// <summary>
/// The outcome of one CLI invocation.
/// </summary>
/// <param name="Outcome">How the invocation ended.</param>
/// <param name="ExitCode">
/// The process exit code. <see langword="null"/> when no process ran or it was killed.
/// </param>
/// <param name="StandardOutput">
/// What the process wrote to standard output, truncated at
/// <see cref="CliRunner.MaxCapturedOutputBytes"/>. Empty for every outcome other than
/// <see cref="CliRunOutcome.Completed"/>.
/// </param>
/// <remarks>
/// <para>
/// This is the one place in the provider layer that carries free text, because a CLI's
/// answer is free text. It is a transient: callers parse it into numbers immediately and
/// drop it. It is never persisted, never logged, and never attached to a
/// <see cref="Core.Models.ProviderUsage"/>.
/// </para>
/// <para>
/// Standard error is redirected and discarded rather than captured. Diagnostics from these
/// CLIs can quote configuration and file paths, and no caller needs them: a non-zero exit
/// code already means "no reading this time".
/// </para>
/// </remarks>
public sealed record CliRunResult(CliRunOutcome Outcome, int? ExitCode, string StandardOutput)
{
    /// <summary>True when the process ran and reported success.</summary>
    public bool IsSuccess => Outcome is CliRunOutcome.Completed && ExitCode == 0;

    /// <summary>The result for an executable that is not installed.</summary>
    public static CliRunResult NotDetected { get; } = new(CliRunOutcome.NotDetected, null, string.Empty);

    /// <summary>The result for an invocation that timed out.</summary>
    public static CliRunResult TimedOut { get; } = new(CliRunOutcome.TimedOut, null, string.Empty);

    /// <summary>The result for an invocation that could not be made.</summary>
    public static CliRunResult Failed { get; } = new(CliRunOutcome.Failed, null, string.Empty);
}
