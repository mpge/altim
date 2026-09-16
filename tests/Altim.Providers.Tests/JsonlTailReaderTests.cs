using System.Text.Json;
using Altim.Providers.Io;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The reader's job is to survive files that are being written to while it reads them.
/// </summary>
public sealed class JsonlTailReaderTests
{
    private static bool TryReadValue(ReadOnlyMemory<byte> utf8Line, out long value)
    {
        value = 0;
        using JsonDocument? document = JsonlTailReader.ParseObject(utf8Line);
        if (document is null)
        {
            return false;
        }

        long? n = JsonValues.ReadInt64(document.RootElement, "n");
        if (n is null)
        {
            return false;
        }

        value = n.Value;
        return true;
    }

    [Fact]
    public void DiscardsAnIncompleteTrailingLine()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLinesWithPartialTail(
            "tail.jsonl",
            ["{\"n\":1}", "{\"n\":2}"],
            "{\"n\":3");

        JsonlReadResult<long> result = JsonlTailReader.ReadTail<long>(path, TryReadValue);

        Assert.Equal([1L, 2L], result.Values);
        Assert.Equal(0, result.LinesSkipped);
    }

    [Fact]
    public void ResumesTheIncompleteLineOnceItIsWhole()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLinesWithPartialTail("tail.jsonl", ["{\"n\":1}"], "{\"n\":2");

        JsonlReadResult<long> first = JsonlTailReader.ReadFrom<long>(path, 0, TryReadValue);
        Assert.Equal([1L], first.Values);

        workspace.Append(path, "}\n");

        JsonlReadResult<long> second = JsonlTailReader.ReadFrom<long>(path, first.EndOffset, TryReadValue);
        Assert.Equal([2L], second.Values);
    }

    [Fact]
    public void SkipsACorruptLineInTheMiddleWithoutLosingTheRest()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteLines(
            "corrupt.jsonl",
            "{\"n\":1}",
            "{\"n\":2, this is not json",
            "{\"n\":3}");

        JsonlReadResult<long> result = JsonlTailReader.ReadTail<long>(path, TryReadValue);

        Assert.Equal([1L, 3L], result.Values);
        Assert.Equal(1, result.LinesSkipped);
    }

    [Fact]
    public void DiscardsThePartialFirstLineOfATailRead()
    {
        using var workspace = new TempWorkspace();

        // A line long enough that an 80-byte tail window must land inside it.
        string padded = "{\"n\":1,\"pad\":\"" + new string('x', 300) + "\"}";
        string path = workspace.WriteLines("big.jsonl", padded, "{\"n\":2}", "{\"n\":3}");

        JsonlReadResult<long> result = JsonlTailReader.ReadTail<long>(path, TryReadValue, tailBytes: 80);

        Assert.True(result.StartedMidLine);
        Assert.Equal([2L, 3L], result.Values);
        Assert.Equal(0, result.LinesSkipped);
    }

    [Fact]
    public void ReadsOnlyTheTailOfALargeFile()
    {
        using var workspace = new TempWorkspace();
        string[] lines = new string[500];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = "{\"n\":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"pad\":\"" + new string('y', 200) + "\"}";
        }

        string path = workspace.WriteLines("large.jsonl", lines);
        long fileLength = new FileInfo(path).Length;

        JsonlReadResult<long> result = JsonlTailReader.ReadTail<long>(path, TryReadValue, tailBytes: 2048);

        Assert.True(result.BytesRead <= 2048);
        Assert.True(fileLength > 90_000, "the fixture must be much larger than the tail window");
        Assert.NotEmpty(result.Values);
        Assert.Equal(499L, result.Values[^1]);
    }

    [Fact]
    public void AMissingFileIsAnEmptyResultRatherThanAThrow()
    {
        using var workspace = new TempWorkspace();
        JsonlReadResult<long> result = JsonlTailReader.ReadTail<long>(workspace.Path_("absent.jsonl"), TryReadValue);

        Assert.Empty(result.Values);
        Assert.Equal(0, result.LinesConsidered);
    }

    [Fact]
    public void HandlesCarriageReturnLineEndings()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.WriteRaw("crlf.jsonl", "{\"n\":1}\r\n{\"n\":2}\r\n");

        JsonlReadResult<long> result = JsonlTailReader.ReadTail<long>(path, TryReadValue);

        Assert.Equal([1L, 2L], result.Values);
    }
}
