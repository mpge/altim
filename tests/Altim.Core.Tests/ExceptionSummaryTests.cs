using Altim.Core.Diagnostics;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// What an exception is allowed to contribute to a log line.
/// </summary>
/// <remarks>
/// <para>
/// PRIVACY.md says Altim never writes file contents or paths into a log, and an exception
/// message breaks that promise on its own. Every <see cref="IOException"/> the framework
/// raises names the file it failed on; <see cref="System.IO.InternalBufferOverflowException"/>
/// names the directory being watched; a provider failure carries whichever path its reader
/// was inside. None of those are Altim's sentences, and all of them end up in
/// <c>%APPDATA%\Altim\altim.log</c>, which is a file users attach to bug reports.
/// </para>
/// <para>
/// So the line carries the exception's <em>type</em> and nothing else. A type name is
/// enough to act on — it is the difference between "the disk refused" and "the format
/// changed" — and it cannot contain anything that came from outside Altim.
/// </para>
/// </remarks>
public sealed class ExceptionSummaryTests
{
    /// <summary>A path of exactly the shape a real exception message would carry.</summary>
    private const string SecretPath = @"C:\Users\someone\projects\acquisition\notes.jsonl";

    [Fact]
    public void AnExceptionIsDescribedByItsTypeAlone() =>
        Assert.Equal("IOException", ExceptionSummary.Describe(new IOException(SecretPath)));

    /// <summary>
    /// The regression this exists for: the message is where the path lives, so the message
    /// is what must not be written.
    /// </summary>
    [Fact]
    public void TheMessageNeverReachesTheLine()
    {
        string described = ExceptionSummary.Describe(
            new UnauthorizedAccessException("Access to the path '" + SecretPath + "' is denied."));

        Assert.DoesNotContain(SecretPath, described, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", described, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UnauthorizedAccessException", described);
    }

    /// <summary>
    /// The buffer-overflow message names the directory being watched, which is a provider's
    /// store and therefore part of the user's filesystem layout.
    /// </summary>
    [Fact]
    public void AWatcherOverflowDoesNotNameTheDirectoryItOverflowedOn()
    {
        var overflow = new InternalBufferOverflowException(
            @"Too many changes at once in directory:C:\Users\someone\.claude\projects\.");

        string described = ExceptionSummary.Describe(overflow);

        Assert.Equal("InternalBufferOverflowException", described);
        Assert.DoesNotContain("projects", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// An inner exception is usually the one that says what actually happened, so its type
    /// is carried too — in brackets, so the outer type still reads first.
    /// </summary>
    [Fact]
    public void AnInnerExceptionContributesItsTypeAndNothingElse() =>
        Assert.Equal(
            "InvalidOperationException (IOException)",
            ExceptionSummary.Describe(new InvalidOperationException("nope", new IOException(SecretPath))));

    /// <summary>
    /// An <see cref="AggregateException"/> is the shape a faulted background task arrives
    /// in, and it can carry several. Each one is named once.
    /// </summary>
    [Fact]
    public void EveryExceptionInAnAggregateIsNamed() =>
        Assert.Equal(
            "AggregateException (IOException, TimeoutException)",
            ExceptionSummary.Describe(new AggregateException(
                new IOException(SecretPath), new TimeoutException(SecretPath))));

    /// <summary>
    /// The walk is bounded. A deeply nested chain is a log line, not a stack trace, and an
    /// unbounded one would be a way to write an arbitrary amount into the file.
    /// </summary>
    [Fact]
    public void TheChainIsBounded()
    {
        Exception nested = new IOException(SecretPath);
        for (int i = 0; i < 20; i++)
        {
            nested = new InvalidOperationException("wrapper", nested);
        }

        string described = ExceptionSummary.Describe(nested);

        Assert.DoesNotContain(SecretPath, described, StringComparison.Ordinal);
        Assert.True(
            described.Length < 200,
            "A bounded chain cannot produce a line this long: " + described);
    }
}
