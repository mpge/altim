using Altim.Providers.Cli;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// A command name is not a file name on Windows, and is exactly a file name everywhere else.
/// Both rules are asserted: the Windows-only cases are skipped off Windows rather than deleted,
/// and each has a counterpart proving the extension search does not happen on other platforms.
/// </summary>
/// <remarks>
/// The npm installation of the Codex CLI drops three files into one directory:
/// <c>codex</c> (a POSIX shell script), <c>codex.cmd</c> and <c>codex.ps1</c>. Only the
/// <c>.cmd</c> can be started by <c>CreateProcess</c>. A resolver that answered with the
/// extensionless file — which is the first thing a naive directory probe finds — hands the
/// process start a shell script, which fails with a bad-format error, and every caller then
/// reports the CLI as "not installed" while it is installed and working.
/// </remarks>
public sealed class ExecutableResolverTests
{
    [WindowsFact]
    public void TheNpmShimLayoutResolvesToTheStartableExtensionRatherThanTheShellScript()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.Write("bin/codex", "#!/bin/sh\nexec node cli.js \"$@\"\n");
        string shim = workspace.Write("bin/codex.cmd", "@echo off\r\nnode cli.js %*\r\n");
        _ = workspace.Write("bin/codex.ps1", "#!/usr/bin/env pwsh\n");

        Assert.True(ExecutableResolver.TryResolveIn([workspace.Path_("bin")], "codex", out string? resolved));
        Assert.Equal(Path.GetFullPath(shim), resolved);
    }

    [UnixFact]
    public void TheNpmShimLayoutResolvesToTheShellScriptOffWindows()
    {
        // The same three files, where the shell script is the one that starts and the
        // .cmd is the useless one. Appending an extension here would be the mirror of
        // the Windows defect.
        using var workspace = new TempWorkspace();
        string script = workspace.Write("bin/codex", "#!/bin/sh\nexec node cli.js \"$@\"\n");
        _ = workspace.Write("bin/codex.cmd", "@echo off\r\nnode cli.js %*\r\n");
        _ = workspace.Write("bin/codex.ps1", "#!/usr/bin/env pwsh\n");

        Assert.True(ExecutableResolver.TryResolveIn([workspace.Path_("bin")], "codex", out string? resolved));
        Assert.Equal(Path.GetFullPath(script), resolved);
    }

    [Fact]
    public void APowerShellScriptIsNotOfferedAsAnExecutable()
    {
        // .ps1 is on PATHEXT for PowerShell, and CreateProcess cannot start one. Resolving
        // to it would only move the bad-format failure one step later.
        using var workspace = new TempWorkspace();
        _ = workspace.Write("bin/tool.ps1", "#!/usr/bin/env pwsh\n");

        Assert.False(ExecutableResolver.TryResolveIn([workspace.Path_("bin")], "tool", out _));
    }

    [Fact]
    public void AnExtensionlessFileIsStillTheLastResort()
    {
        // On a machine where nothing but the bare name exists, answering with it is better
        // than reporting the command missing: it is what a non-Windows host has, and a
        // Windows host with a real extensionless image can still start it.
        using var workspace = new TempWorkspace();
        string bare = workspace.Write("bin/onlybare", "#!/bin/sh\n");

        Assert.True(ExecutableResolver.TryResolveIn([workspace.Path_("bin")], "onlybare", out string? resolved));
        Assert.Equal(Path.GetFullPath(bare), resolved);
    }

    [WindowsFact]
    public void ExtensionsAreTriedInTheOrderTheShellWouldTryThem()
    {
        using var workspace = new TempWorkspace();
        _ = workspace.Write("bin/multi.cmd", "@echo off\r\n");
        string exe = workspace.Write("bin/multi.exe", "MZ");

        Assert.True(ExecutableResolver.TryResolveIn([workspace.Path_("bin")], "multi", out string? resolved));

        // PATHEXT lists .EXE before .CMD, and so does the fallback list.
        Assert.Equal(Path.GetFullPath(exe), resolved);
    }

    [UnixFact]
    public void NoExtensionIsEverAppendedOffWindows()
    {
        // A directory holding only multi.cmd and multi.exe holds no command called
        // "multi" anywhere but Windows, and saying otherwise would hand the process
        // start a file the kernel cannot execute.
        using var workspace = new TempWorkspace();
        _ = workspace.Write("bin/multi.cmd", "@echo off\r\n");
        _ = workspace.Write("bin/multi.exe", "MZ");

        Assert.False(ExecutableResolver.TryResolveIn([workspace.Path_("bin")], "multi", out _));
    }

    [Fact]
    public void EarlierDirectoriesWinOverLaterOnes()
    {
        // Name the files the way the running platform names commands, so the test is
        // about ordering rather than about extensions.
        string name = OperatingSystem.IsWindows() ? "dup.cmd" : "dup";
        using var workspace = new TempWorkspace();
        string first = workspace.Write($"one/{name}", "@echo off\r\n");
        _ = workspace.Write($"two/{name}", "@echo off\r\n");

        Assert.True(
            ExecutableResolver.TryResolveIn([workspace.Path_("one"), workspace.Path_("two")], "dup", out string? resolved));
        Assert.Equal(Path.GetFullPath(first), resolved);
    }

    [Fact]
    public void ACommandThatAlreadyCarriesAnExtensionIsNotGivenAnother()
    {
        using var workspace = new TempWorkspace();
        string named = workspace.Write("bin/tool.cmd", "@echo off\r\n");

        Assert.True(ExecutableResolver.TryResolveIn([workspace.Path_("bin")], "tool.cmd", out string? resolved));
        Assert.Equal(Path.GetFullPath(named), resolved);
    }

    [Fact]
    public void AnUnreadableOrMalformedSearchDirectoryIsSkippedRatherThanFatal()
    {
        using var workspace = new TempWorkspace();
        string shim = workspace.Write(OperatingSystem.IsWindows() ? "bin/codex.cmd" : "bin/codex", "@echo off\r\n");

        Assert.True(
            ExecutableResolver.TryResolveIn(["\0not a directory", workspace.Path_("bin")], "codex", out string? resolved));
        Assert.Equal(Path.GetFullPath(shim), resolved);
    }

    [Fact]
    public void NothingResolvesOutOfAnEmptySearchPath() =>
        Assert.False(ExecutableResolver.TryResolveIn([], "codex", out _));
}
