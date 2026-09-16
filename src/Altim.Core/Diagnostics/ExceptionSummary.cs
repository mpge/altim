using System.Text;

namespace Altim.Core.Diagnostics;

/// <summary>
/// One short phrase describing an exception, built only from type names.
/// </summary>
/// <remarks>
/// <para>
/// PRIVACY.md promises that Altim never writes file contents or paths into a log, and an
/// exception <em>message</em> breaks that promise without anybody deciding to. Every
/// <see cref="IOException"/> the framework raises names the file it failed on;
/// <see cref="InternalBufferOverflowException"/> names the directory a watcher was watching,
/// which is a provider's store; a failed provider read carries whichever path its reader was
/// inside. None of those sentences are Altim's, and <c>altim.log</c> is a file users attach
/// to bug reports.
/// </para>
/// <para>
/// A type name is enough to act on. "IOException" against a watcher is a disk or a
/// permission; "JsonException" against a provider is a format change; the two lead to
/// different work, and neither needs the path to say so. What the message would add is
/// precisely the part that cannot be written down.
/// </para>
/// <para>
/// The walk is bounded. A log line is not a stack trace, and an unbounded chain would be a
/// way for a deeply nested exception to write an arbitrary amount into the file.
/// </para>
/// </remarks>
public static class ExceptionSummary
{
    /// <summary>How many levels of inner exception are named.</summary>
    public const int MaxDepth = 3;

    /// <summary>How many of an <see cref="AggregateException"/>'s exceptions are named.</summary>
    public const int MaxAggregated = 4;

    /// <summary>
    /// Names the exception's type, and the types of whatever it wraps.
    /// </summary>
    /// <param name="error">The exception to describe. Never <see langword="null"/>.</param>
    /// <returns>
    /// A phrase such as <c>IOException</c> or
    /// <c>InvalidOperationException (IOException)</c>. Never contains
    /// <see cref="Exception.Message"/>, a stack trace, or anything else that came from
    /// outside Altim.
    /// </returns>
    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var text = new StringBuilder();
        Append(text, error, MaxDepth);
        return text.ToString();
    }

    private static void Append(StringBuilder text, Exception error, int depthLeft)
    {
        _ = text.Append(error.GetType().Name);

        if (depthLeft <= 0)
        {
            return;
        }

        if (error is AggregateException aggregate)
        {
            AppendAggregate(text, aggregate, depthLeft);
            return;
        }

        if (error.InnerException is { } inner)
        {
            _ = text.Append(" (");
            Append(text, inner, depthLeft - 1);
            _ = text.Append(')');
        }
    }

    private static void AppendAggregate(StringBuilder text, AggregateException aggregate, int depthLeft)
    {
        IReadOnlyList<Exception> inner = aggregate.InnerExceptions;
        if (inner.Count == 0)
        {
            return;
        }

        _ = text.Append(" (");

        int named = Math.Min(inner.Count, MaxAggregated);
        for (int i = 0; i < named; i++)
        {
            if (i > 0)
            {
                _ = text.Append(", ");
            }

            Append(text, inner[i], depthLeft - 1);
        }

        if (inner.Count > named)
        {
            _ = text.Append(", and more");
        }

        _ = text.Append(')');
    }
}
