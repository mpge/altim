using System.Text;
using System.Text.Json;
using Altim.Providers.Claude;
using Altim.Providers.Claude.StatusLine;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The one component in the provider layer that writes to a file the user owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test here goes through <see cref="SandboxedInstaller"/>, which refuses to run
/// against anything outside its own temporary directory.</b> A test suite that could reach
/// a real <c>settings.json</c> is one bad default away from rewriting the machine's Claude
/// Code configuration on a green build, and the person whose file it is would have no idea
/// which run did it.
/// </para>
/// <para>
/// The behaviour under test is the set of promises the installer makes: ask first, merge
/// rather than overwrite, never take over an existing status line, keep the file's own
/// formatting, and keep a backup that an install-and-revert cycle cannot destroy.
/// </para>
/// </remarks>
public sealed class StatusLineInstallerTests
{
    private const string Command = "\"C:\\Program Files\\Altim\\altim.exe\" altim-statusline";

    [Fact]
    public void ACommandWithoutTheMarkerIsRefusedAtConstruction() =>
        Assert.Throws<ArgumentException>(static () => new StatusLineInstaller("some-other-tool --status"));

    [Fact]
    public void ADryRunPlansTheChangeAndWritesNothing()
    {
        using var sandbox = new SandboxedInstaller();
        sandbox.WriteSettings("""{ "theme": "dark" }""");

        StatusLineInstallResult result = sandbox.Installer.Install();

        Assert.Equal(StatusLineInstallOutcome.WouldInstall, result.Outcome);
        Assert.True(result.DryRun);
        Assert.Null(result.BackupPath);
        Assert.Equal("""{ "theme": "dark" }""", sandbox.ReadSettings());
    }

    [Fact]
    public void InstallingIsTheDefaultOffAndTakesAnExplicitDecision()
    {
        using var sandbox = new SandboxedInstaller();
        sandbox.WriteSettings("{}");

        // The parameterless call is the plan. Nothing is written until dryRun is false.
        Assert.True(sandbox.Installer.Install().DryRun);
        Assert.False(sandbox.Installer.Install(dryRun: false).DryRun);
    }

    [Fact]
    public void AnExistingStatusLineIsNeverReplaced()
    {
        using var sandbox = new SandboxedInstaller();
        const string Theirs = """
            {
              "statusLine": { "type": "command", "command": "my-own-prompt.sh" }
            }
            """;
        sandbox.WriteSettings(Theirs);

        StatusLineInstallResult result = sandbox.Installer.Install(dryRun: false);

        Assert.Equal(StatusLineInstallOutcome.RefusedExistingStatusLine, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(Theirs, sandbox.ReadSettings());
    }

    [Fact]
    public void ARevertLeavesAStatusLineThatIsNotAltimsAlone()
    {
        using var sandbox = new SandboxedInstaller();
        const string Theirs = """{ "statusLine": { "type": "command", "command": "my-own-prompt.sh" } }""";
        sandbox.WriteSettings(Theirs);

        Assert.Equal(StatusLineInstallOutcome.NothingToRevert, sandbox.Installer.Revert(dryRun: false).Outcome);
        Assert.Equal(Theirs, sandbox.ReadSettings());
    }

    [Fact]
    public void InstallingMergesIntoTheFileAndLeavesEverythingElseAlone()
    {
        using var sandbox = new SandboxedInstaller();
        sandbox.WriteSettings(
            """
            {
              "theme": "dark",
              "hooks": { "PreToolUse": [ { "matcher": "Bash" } ] }
            }
            """);

        StatusLineInstallResult result = sandbox.Installer.Install(dryRun: false);

        Assert.Equal(StatusLineInstallOutcome.Installed, result.Outcome);

        string after = sandbox.ReadSettings();
        Assert.Contains("\"theme\": \"dark\"", after, StringComparison.Ordinal);
        Assert.Contains("PreToolUse", after, StringComparison.Ordinal);
        Assert.Contains(StatusLineInstaller.CommandMarker, after, StringComparison.Ordinal);
        Assert.Equal(StatusLineInstallOutcome.AlreadyInstalled, sandbox.Installer.Install(dryRun: false).Outcome);
    }

    [Fact]
    public void TheUsersCommentsAndFormattingSurviveAnInstallAndARevert()
    {
        // Settings files are edited by hand and kept in dotfiles repositories. Reserialising
        // the document loses the comments and reflows every line, so a one-key change
        // arrives as a whole-file diff.
        using var sandbox = new SandboxedInstaller();
        const string Original = """
            {
                // The theme is deliberately dark; do not change it.
                "theme": "dark",

                "env": {
                    "FOO": "bar"
                }
            }
            """;
        sandbox.WriteSettings(Original);

        _ = sandbox.Installer.Install(dryRun: false);
        string installed = sandbox.ReadSettings();

        Assert.Contains("// The theme is deliberately dark", installed, StringComparison.Ordinal);
        Assert.Contains("    \"theme\": \"dark\",", installed, StringComparison.Ordinal);
        Assert.Contains("\"statusLine\"", installed, StringComparison.Ordinal);

        _ = sandbox.Installer.Revert(dryRun: false);

        Assert.Equal(Original, sandbox.ReadSettings());
    }

    [Fact]
    public void AnInstallAndRevertCycleCannotDestroyTheOriginalBackup()
    {
        // With one fixed backup name the revert's backup overwrote the install's, and the
        // only copy of what the user originally had was replaced by Altim's own version.
        using var sandbox = new SandboxedInstaller();
        const string Original = """{ "theme": "dark" }""";
        sandbox.WriteSettings(Original);

        StatusLineInstallResult installed = sandbox.Installer.Install(dryRun: false);
        StatusLineInstallResult reverted = sandbox.Installer.Revert(dryRun: false);

        Assert.NotNull(installed.BackupPath);
        Assert.NotNull(reverted.BackupPath);
        Assert.NotEqual(installed.BackupPath, reverted.BackupPath);

        // The first backup still holds what the user had before Altim touched anything.
        Assert.Equal(Original, File.ReadAllText(installed.BackupPath));
        Assert.DoesNotContain(StatusLineInstaller.CommandMarker, File.ReadAllText(installed.BackupPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingSettingsFileIsCreatedWithNothingButTheStatusLine()
    {
        using var sandbox = new SandboxedInstaller();

        StatusLineInstallResult result = sandbox.Installer.Install(dryRun: false);

        Assert.Equal(StatusLineInstallOutcome.Installed, result.Outcome);
        Assert.Null(result.BackupPath);
        Assert.Contains(StatusLineInstaller.CommandMarker, sandbox.ReadSettings(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsFileThatIsNotAJsonObjectIsNeverWrittenOver()
    {
        using var sandbox = new SandboxedInstaller();
        const string Broken = "[ this is not settings ]";
        sandbox.WriteSettings(Broken);

        Assert.Equal(StatusLineInstallOutcome.SettingsUnreadable, sandbox.Installer.Install(dryRun: false).Outcome);
        Assert.Equal(Broken, sandbox.ReadSettings());
    }

    [Fact]
    public void RevertingWhenThereIsNoSettingsFileDoesNothing()
    {
        using var sandbox = new SandboxedInstaller();

        Assert.Equal(StatusLineInstallOutcome.NothingToRevert, sandbox.Installer.Revert(dryRun: false).Outcome);
        Assert.False(File.Exists(sandbox.SettingsPath));
    }

    [Fact]
    public void TheReplacementIsAtomicAndLeavesNoHalfWrittenFileBehind()
    {
        // The new contents are written beside the target and moved into place, so a crash
        // or a full disk mid-write cannot leave the user with a settings file that Claude
        // Code will refuse to parse. Nothing of the mechanism survives the call.
        using var sandbox = new SandboxedInstaller();
        sandbox.WriteSettings("""{ "theme": "dark" }""");

        StatusLineInstallResult result = sandbox.Installer.Install(dryRun: false);

        Assert.Equal(StatusLineInstallOutcome.Installed, result.Outcome);
        Assert.DoesNotContain(
            Directory.GetFiles(Path.GetDirectoryName(sandbox.SettingsPath)!),
            static path => path.EndsWith(".altim-tmp", StringComparison.OrdinalIgnoreCase));

        // And what is in place is a whole document, not a prefix of one.
        using JsonDocument parsed = JsonDocument.Parse(sandbox.ReadSettings());
        Assert.Equal("dark", parsed.RootElement.GetProperty("theme").GetString());
        Assert.Contains(
            StatusLineInstaller.CommandMarker,
            parsed.RootElement.GetProperty(StatusLineInstaller.SettingsProperty).GetProperty("command").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackupIsTheUsersFileByteForByteAndIsNamedInTheResult()
    {
        // A backup that reformatted, re-encoded or renamed anything would be a second
        // version of the file rather than a way back to the first one.
        using var sandbox = new SandboxedInstaller();
        byte[] original = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("{\r\n  \"theme\": \"dark\"\r\n}\r\n")];
        File.WriteAllBytes(sandbox.SettingsPath, original);

        StatusLineInstallResult result = sandbox.Installer.Install(dryRun: false);

        Assert.NotNull(result.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(result.BackupPath));
        Assert.StartsWith(
            Path.GetFullPath(sandbox.SettingsPath),
            Path.GetFullPath(result.BackupPath),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDirectoryOverrideIsWhatKeepsThisSuiteOffTheRealSettingsFile()
    {
        // The guard the whole file rests on, asserted from both sides. With the override
        // the installer cannot leave the temporary directory; without it, it resolves the
        // machine's own Claude Code settings file — which is exactly why the dry run is the
        // default, and why this test only ever plans.
        using var workspace = new TempWorkspace();

        string? sandboxed = new StatusLineInstaller(Command, workspace.Root).Install().SettingsPath;
        Assert.NotNull(sandboxed);
        Assert.StartsWith(
            Path.GetFullPath(workspace.Root),
            Path.GetFullPath(sandboxed),
            StringComparison.OrdinalIgnoreCase);

        StatusLineInstallResult unsandboxed = new StatusLineInstaller(Command).Install();

        Assert.True(unsandboxed.DryRun);
        Assert.Null(unsandboxed.BackupPath);
        Assert.NotEqual(StatusLineInstallOutcome.Installed, unsandboxed.Outcome);

        if (unsandboxed.SettingsPath is { } real)
        {
            Assert.DoesNotContain(
                Path.GetFullPath(workspace.Root),
                Path.GetFullPath(real),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AByteOrderMarkAndCrlfLineEndingsAreLeftAsTheyWere()
    {
        using var sandbox = new SandboxedInstaller();
        byte[] original = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("{\r\n  \"theme\": \"dark\"\r\n}\r\n")];
        File.WriteAllBytes(sandbox.SettingsPath, original);

        _ = sandbox.Installer.Install(dryRun: false);
        byte[] after = File.ReadAllBytes(sandbox.SettingsPath);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, after.Take(3));
        Assert.Contains("\r\n  \"statusLine\"", Encoding.UTF8.GetString(after), StringComparison.Ordinal);
        Assert.DoesNotContain("\n  \"theme\": \"dark\"\n", Encoding.UTF8.GetString(after).Replace("\r\n", "\r", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// An installer that is structurally unable to touch a real settings file.
    /// </summary>
    /// <remarks>
    /// The config root is overridden to a temporary directory, and the path the installer
    /// resolves is checked against that directory <b>before</b> any test is allowed to make
    /// a non-dry-run call. A regression that made the override optional, or that resolved
    /// the real root anyway, fails here rather than in the user's home directory.
    /// </remarks>
    private sealed class SandboxedInstaller : IDisposable
    {
        private readonly TempWorkspace _workspace = new();

        public SandboxedInstaller()
        {
            Installer = new StatusLineInstaller(Command, _workspace.Root);
            SettingsPath = ClaudePaths.SettingsFile(_workspace.Root);

            // The plan names the file it would write. If that is not inside this test's own
            // directory, nothing else in the test runs.
            string? planned = Installer.Install().SettingsPath;
            Assert.NotNull(planned);
            Assert.StartsWith(
                Path.GetFullPath(_workspace.Root),
                Path.GetFullPath(planned),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.GetFullPath(SettingsPath), Path.GetFullPath(planned));
        }

        public StatusLineInstaller Installer { get; }

        public string SettingsPath { get; }

        public void WriteSettings(string content) => File.WriteAllText(SettingsPath, content);

        public string ReadSettings() => File.ReadAllText(SettingsPath);

        public void Dispose() => _workspace.Dispose();
    }
}
