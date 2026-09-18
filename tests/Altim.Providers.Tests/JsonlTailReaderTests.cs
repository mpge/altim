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

    /// <summary>
    /// A slice many windows long is read whole, in order, in one pass.
    /// </summary>
    /// <remarks>
    /// The reader holds 64KB at a time and slides, so a line lands across a window boundary
    /// roughly every 64KB of this fixture. Counting the values is what catches a carry-over
    /// that loses the cut line, and comparing them in order is what catches one that reads
    /// the remainder twice.
    /// </remarks>
    [Fact]
    public void ReadsEveryLineOfASliceThatIsManyWindowsLong()
    {
        using var workspace = new TempWorkspace();
        string[] lines = new string[3000];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = "{\"n\":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"pad\":\"" + new string('z', 120) + "\"}";
        }

        string path = workspace.WriteLines("windows.jsonl", lines);
        long fileLength = new FileInfo(path).Length;
        Assert.True(fileLength > 6 * 64 * 1024, "the fixture must span several windows");

        JsonlReadResult<long> result = JsonlTailReader.ReadFrom<long>(path, 0, TryReadValue);

        Assert.Equal(3000, result.Values.Count);
        Assert.Equal(0L, result.Values[0]);
        Assert.Equal(2999L, result.Values[^1]);
        Assert.Equal([.. Enumerable.Range(0, 3000).Select(static i => (long)i)], result.Values);
        Assert.Equal(fileLength, result.EndOffset);
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// A line that begins inside one window and ends in the next is read once and whole.
    /// </summary>
    /// <remarks>
    /// The padding puts the second line's opening brace four bytes short of the 64KB window,
    /// so the line is cut by the refill. A carry-over that drops the head of the line leaves
    /// a fragment the parser rejects, and this asserts the line was neither lost nor counted
    /// as malformed.
    /// </remarks>
    [Fact]
    public void ALineCutInHalfByTheWindowBoundaryIsReadOnceAndWhole()
    {
        using var workspace = new TempWorkspace();

        const int Window = 64 * 1024;
        const string Second = "{\"n\":2}";
        int firstLineLength = Window - 4;
        int padding = firstLineLength - "{\"n\":1,\"pad\":\"\"}".Length - 1;
        string first = "{\"n\":1,\"pad\":\"" + new string('p', padding) + "\"}";

        string path = workspace.WriteLines("straddle.jsonl", first, Second, "{\"n\":3}");
        Assert.Equal(firstLineLength, first.Length + 1);

        JsonlReadResult<long> result = JsonlTailReader.ReadFrom<long>(path, 0, TryReadValue);

        Assert.Equal([1L, 2L, 3L], result.Values);
        Assert.Equal(0, result.LinesSkipped);
        Assert.Equal(3, result.LinesConsidered);
    }

    /// <summary>
    /// A single line longer than the window is read, not skipped and not stalled on.
    /// </summary>
    /// <remarks>
    /// The window doubles until the line fits. Without that the reader would return no
    /// complete line, leave its cursor where it started, and read the same bytes again on
    /// every pass for as long as that file existed.
    /// </remarks>
    [Fact]
    public void ALineLongerThanTheWindowIsReadRatherThanStalledOn()
    {
        using var workspace = new TempWorkspace();
        string huge = "{\"n\":7,\"pad\":\"" + new string('h', 200 * 1024) + "\"}";
        string path = workspace.WriteLines("huge.jsonl", huge, "{\"n\":8}");
        long fileLength = new FileInfo(path).Length;

        JsonlReadResult<long> result = JsonlTailReader.ReadFrom<long>(path, 0, TryReadValue);

        Assert.Equal([7L, 8L], result.Values);
        Assert.Equal(fileLength, result.EndOffset);
    }

    /// <summary>
    /// A tail read discards its leading fragment once, however many windows the fragment
    /// spans, and every line after it survives.
    /// </summary>
    /// <remarks>
    /// Two separate mistakes are caught here, and the fixture is shaped for both: the
    /// fragment is longer than one window, and what follows it is longer than one window
    /// too. A reader that forgot it was mid-fragment would parse the fragment's remainder
    /// as a line and count it malformed; one that re-entered the discard on every refill
    /// would swallow the first line of every window after the first, which is a silent
    /// undercount rather than an error.
    /// </remarks>
    [Fact]
    public void ATailFragmentLongerThanTheWindowIsDiscardedExactlyOnce()
    {
        using var workspace = new TempWorkspace();

        const int Window = 64 * 1024;
        string[] lines = new string[10_000];
        lines[0] = "{\"n\":0,\"pad\":\"" + new string('g', 200 * 1024) + "\"}";
        for (int i = 1; i < lines.Length; i++)
        {
            lines[i] = "{\"n\":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        string path = workspace.WriteLines("tailfragment.jsonl", lines);

        // Everything after the first line, plus enough of the first line that the fragment
        // cannot fit in one window either.
        int afterFirst = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            afterFirst += lines[i].Length + 1;
        }

        int tail = afterFirst + (2 * Window);
        Assert.True(afterFirst > Window, "the lines after the fragment must span several windows");

        JsonlReadResult<long> result = JsonlTailReader.ReadTail<long>(path, TryReadValue, tailBytes: tail);

        Assert.True(result.StartedMidLine);
        Assert.Equal(lines.Length - 1, result.Values.Count);
        Assert.Equal(1L, result.Values[0]);
        Assert.Equal(9999L, result.Values[^1]);
        Assert.Equal([.. Enumerable.Range(1, lines.Length - 1).Select(static i => (long)i)], result.Values);
        Assert.Equal(0, result.LinesSkipped);
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
