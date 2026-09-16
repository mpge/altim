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
}
