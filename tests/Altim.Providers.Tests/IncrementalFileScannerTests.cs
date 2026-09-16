using System.Text.Json;
using Altim.Providers.Io;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// Re-reading gigabytes on every poll is the failure this class exists to prevent.
/// </summary>
public sealed class IncrementalFileScannerTests
{
    private static bool TryReadValue(ReadOnlyMemory<byte> utf8Line, out long value)
    {
        value = 0;
        using JsonDocument? document = JsonlTailReader.ParseObject(utf8Line);
        long? n = document is null ? null : JsonValues.ReadInt64(document.RootElement, "n");
        if (n is null)
        {
            return false;
        }

        value = n.Value;
        return true;
    }

    [Fact]
    public void ReadsOnlyTheBytesAppendedSinceTheLastScan()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines("log.jsonl", "{\"n\":1}", "{\"n\":2}");

        var scanner = new IncrementalFileScanner();
        Assert.Equal([1L, 2L], scanner.ScanNew<long>(path, TryReadValue).Values);

        workspace.Append(path, "{\"n\":3}\n");

        JsonlReadResult<long> second = scanner.ScanNew<long>(path, TryReadValue);
        Assert.Equal([3L], second.Values);
        Assert.True(second.BytesRead < 20);
    }

    [Fact]
    public void AnUnchangedFileIsNotReadAtAll()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines("log.jsonl", "{\"n\":1}");

        var scanner = new IncrementalFileScanner();
        _ = scanner.ScanNew<long>(path, TryReadValue);

        JsonlReadResult<long> second = scanner.ScanNew<long>(path, TryReadValue);

        Assert.Empty(second.Values);
        Assert.Equal(0, second.BytesRead);
    }

    [Fact]
    public void AShrunkFileIsTreatedAsReplacedAndReadFromTheStart()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines("log.jsonl", "{\"n\":1}", "{\"n\":2}", "{\"n\":3}");

        var scanner = new IncrementalFileScanner();
        _ = scanner.ScanNew<long>(path, TryReadValue);

        _ = workspace.WriteLines("log.jsonl", "{\"n\":9}");

        JsonlReadResult<long> after = scanner.ScanNew<long>(path, TryReadValue);
        Assert.Equal([9L], after.Values);
    }

    [Fact]
    public void ACursorAlwaysLandsOnALineBoundary()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLinesWithPartialTail("log.jsonl", ["{\"n\":1}"], "{\"n\":2");

        var scanner = new IncrementalFileScanner();
        _ = scanner.ScanNew<long>(path, TryReadValue);

        Assert.True(scanner.TryGetCursor(path, out FileScanCursor cursor));
        Assert.Equal(8, cursor.Offset);

        workspace.Append(path, "}\n");
        Assert.Equal([2L], scanner.ScanNew<long>(path, TryReadValue).Values);
    }

    [Fact]
    public void ForgettingAFileMakesTheNextScanStartOver()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines("log.jsonl", "{\"n\":1}");

        var scanner = new IncrementalFileScanner();
        _ = scanner.ScanNew<long>(path, TryReadValue);
        Assert.True(scanner.Forget(path));

        Assert.Equal([1L], scanner.ScanNew<long>(path, TryReadValue).Values);
    }

    [Fact]
    public void AMissingFileIsForgottenRatherThanThrowing()
    {
        using var workspace = new TempWorkspace();
        var scanner = new IncrementalFileScanner();

        Assert.Empty(scanner.ScanNew<long>(workspace.Path_("gone.jsonl"), TryReadValue).Values);
        Assert.Equal(0, scanner.TrackedFileCount);
    }

    [Fact]
    public void ABudgetStopsAHugeAppendFromBeingReadInOneGo()
    {
        using var workspace = new TempWorkspace();
        string[] lines = new string[400];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = "{\"n\":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"pad\":\"" + new string('z', 100) + "\"}";
        }

        string path = workspace.WriteLines("huge.jsonl", lines);

        var scanner = new IncrementalFileScanner(maxBytesPerFile: 4096);
        JsonlReadResult<long> first = scanner.ScanNew<long>(path, TryReadValue);

        Assert.True(first.Truncated);
        Assert.True(first.BytesRead <= 4096);

        JsonlReadResult<long> second = scanner.ScanNew<long>(path, TryReadValue);
        Assert.NotEmpty(second.Values);
        Assert.True(second.Values[0] > first.Values[^1]);
    }
}
