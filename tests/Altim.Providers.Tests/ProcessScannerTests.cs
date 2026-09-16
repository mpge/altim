using System.Reflection;
using Altim.Core.Models;
using Altim.Providers.Claude;
using Altim.Providers.Codex;
using Altim.Providers.Diagnostics;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// Process detection, and the rule that makes it safe.
/// </summary>
public sealed class ProcessScannerTests
{
    [Fact]
    public async Task AnExecutableThatIsNotRunningYieldsNothing()
    {
        var scanner = new ProcessScanner(
            new Dictionary<string, string> { ["altim-no-such-process-ffff"] = "codex" });

        Assert.Empty(await scanner.ScanAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExcludedHelperNameIsNeverReported()
    {
        // The name is in both tables. Exclusion wins, so a helper that ships under its own
        // executable name cannot be counted as a session.
        var scanner = new ProcessScanner(
            new Dictionary<string, string> { ["altim-helper-ffff"] = "claude" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "altim-helper-ffff" });

        Assert.Empty(await scanner.ScanAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScanningIsCancellable()
    {
        var scanner = new ProcessScanner(new Dictionary<string, string> { ["altim-none"] = "codex" });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scanner.ScanAsync(cts.Token));
    }

    [Fact]
    public void TheDefaultScannersWatchTheDocumentedExecutableNames()
    {
        Assert.Contains("codex", CodexUsageProvider.CreateDefaultProcessScanner().WatchedProcessNames);
        Assert.Contains("claude", ClaudeUsageProvider.CreateDefaultProcessScanner().WatchedProcessNames);
    }

    [Fact]
    public void ADetectedProcessCannotCarryACommandLine()
    {
        // Enumerating processes on the verification machine exposed a third-party tool
        // passing an API key in plaintext in its arguments. The guarantee that Altim cannot
        // ingest one is structural: there is no field to put it in.
        string[] properties = [.. typeof(DetectedProcess)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static p => p.Name)];

        Assert.Equal(["ProcessId", "ProviderId", "ExecutableName", "StartedAt"], properties);
    }

    [Fact]
    public void HasProcessMatchesOnProviderId()
    {
        IReadOnlyList<DetectedProcess> processes = [new DetectedProcess(1, "codex", "codex", null)];

        Assert.True(ProcessScanner.HasProcess(processes, "codex"));
        Assert.False(ProcessScanner.HasProcess(processes, "claude"));
        Assert.False(ProcessScanner.HasProcess([], "codex"));
    }
}
