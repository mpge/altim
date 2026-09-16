using Altim.Providers.Cli;
using Altim.Providers.Codex;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// "Not installed" has to be an answer, not an exception.
/// </summary>
public sealed class CliRunnerTests
{
    private const string AbsentCommand = "altim-no-such-cli-ffffffff";

    [Fact]
    public async Task AMissingBinaryIsReportedAsNotDetectedRatherThanThrowing()
    {
        var runner = new CliRunner();

        CliRunResult result = await runner.RunAsync(AbsentCommand, ["--version"], TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(CliRunOutcome.NotDetected, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Null(result.ExitCode);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public void ExistenceIsDecidedWithoutStartingAProcess() =>
        Assert.False(new CliRunner().Exists(AbsentCommand));

    [Fact]
    public void AnAbsolutePathThatDoesNotExistDoesNotResolve() =>
        Assert.False(ExecutableResolver.TryResolve(Path.Combine(Path.GetTempPath(), "altim-absent", "nope.exe"), out _));

    [Fact]
    public void AnExistingFileResolvesToItsFullPath()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.Write("tool.cmd", "@echo off");

        Assert.True(ExecutableResolver.TryResolve(path, out string? resolved));
        Assert.Equal(Path.GetFullPath(path), resolved);
    }

    [Fact]
    public async Task AProviderTreatsAMissingCliAsNotDetected()
    {
        // The end of the chain that starts at ExecutableResolver: a machine without Codex
        // installed produces a settled "not detected", not an error status.
        using var workspace = new TempWorkspace();
        using var provider = new CodexUsageProvider(
            CodexOptions.Default,
            new StubAppServerClient(Altim.Providers.Codex.AppServer.CodexLiveResult.NotDetected, isAvailable: false),
            new FakeCliRunner { CommandExists = false },
            new FakeProcessMonitor(),
            Path.Combine(workspace.Root, "absent-home"),
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.Equal(Core.Models.ProviderStatus.NotDetected, (await provider.GetUsageAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task AFileThatResolvedButCannotBeStartedIsAFailureNotAMissingInstallation()
    {
        // The two mean different things to the UI: "not detected" invites the user to
        // install something they already have, and hides a real fault.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The bad-image-format failure mode is a Windows one.");

        using var workspace = new TempWorkspace();
        string path = workspace.Write("broken.exe", "this is not a portable executable");

        CliRunResult result = await new CliRunner()
            .RunAsync(path, ["--version"], TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(CliRunOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task AChildThatOutrunsTheCaptureCapIsStillDrainedSoItCannotBlock()
    {
        // The child writes far more than the cap. A reader that stopped at the cap would
        // leave the pipe full, the child would block on its next write, and a working
        // command would be reported as a timeout.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The fixture is a batch script.");

        using var workspace = new TempWorkspace();
        string path = workspace.WriteRaw(
            "chatty.cmd",
            "@echo off\r\nfor /L %%i in (1,1,2000) do @echo "
            + new string('x', 120)
            + "\r\n");

        var runner = new CliRunner(capturedOutputLimitBytes: 1024);
        CliRunResult result = await runner.RunAsync(path, [], TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(CliRunOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1024, result.StandardOutput.Length);
    }

    [Fact]
    public void TheWorkingDirectoryHandedToAProviderCliIsStableAndIsNotATempFolder()
    {
        // Claude Code registers the directory it is run in as a project. Running the
        // headless summary from the temp folder would put a temp path in the user's own
        // project list, and a fresh one every time if the directory moved.
        string first = CliRunner.NeutralWorkingDirectory();
        string second = CliRunner.NeutralWorkingDirectory();

        Assert.Equal(first, second);
        Assert.True(Directory.Exists(first));

        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        Assert.NotEqual(temp, Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)));
    }
}
