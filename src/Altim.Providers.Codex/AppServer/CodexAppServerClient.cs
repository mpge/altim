using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Altim.Providers.Cli;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Io;

namespace Altim.Providers.Codex.AppServer;

/// <summary>
/// Speaks line-delimited JSON-RPC to <c>codex app-server</c> over standard input and output.
/// </summary>
/// <remarks>
/// <para>
/// The exchange is fixed: <c>initialize</c>, then the <c>initialized</c> notification, then
/// <c>account/rateLimits/read</c> and <c>account/usage/read</c>. Responses are matched by
/// request id, and notifications arriving in between are ignored rather than mistaken for
/// answers.
/// </para>
/// <para>
/// <b>This is local IPC but it is not offline.</b> The CLI calls OpenAI's backend with the
/// user's stored ChatGPT token, and hard-errors under API-key authentication. Altim never
/// opens <c>auth.json</c> and never calls an OpenAI endpoint itself: the CLI owns
/// authentication, and this client only asks it a question. The caller decides whether the
/// question may be asked at all, and how often.
/// </para>
/// <para>
/// The interface is marked experimental by its vendor, so everything it returns is
/// best-effort and every field is optional.
/// </para>
/// <para>
/// Standard error is drained for the same reason standard output is: an unread pipe fills
/// at about 64 KB and blocks the child's next write, and an app-server that logs a warning
/// would then be killed on a timeout and reported as broken.
/// </para>
/// </remarks>
public sealed class CodexAppServerClient : ICodexAppServerClient
{
    /// <summary>The request id used for <c>initialize</c>.</summary>
    public const int InitializeId = 1;

    /// <summary>The request id used for <c>account/rateLimits/read</c>.</summary>
    public const int RateLimitsId = 2;

    /// <summary>The request id used for <c>account/usage/read</c>.</summary>
    public const int UsageId = 3;

    private const string DefaultCommand = "codex";
    private const int MaxLinesToRead = 512;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };

    private readonly string _command;
    private readonly string _clientName;
    private readonly string _clientVersion;

    /// <summary>
    /// Creates a client.
    /// </summary>
    /// <param name="command">The Codex CLI command name or path.</param>
    /// <param name="clientName">The name this client announces in <c>initialize</c>.</param>
    /// <param name="clientVersion">The version this client announces in <c>initialize</c>.</param>
    public CodexAppServerClient(string command = DefaultCommand, string clientName = "altim", string clientVersion = "1.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        _command = command;
        _clientName = clientName;
        _clientVersion = clientVersion;
    }

    /// <inheritdoc />
    public bool IsAvailable => ExecutableResolver.TryResolve(_command, out _);

    /// <summary>
    /// Performs the JSON-RPC exchange over an already-connected pair of streams.
    /// </summary>
    /// <param name="requests">Where requests are written, one JSON document per line.</param>
    /// <param name="responses">Where responses are read from, one JSON document per line.</param>
    /// <param name="clientName">The name announced in <c>initialize</c>.</param>
    /// <param name="clientVersion">The version announced in <c>initialize</c>.</param>
    /// <param name="ct">Cancels the exchange.</param>
    /// <returns>The outcome of the exchange.</returns>
    /// <remarks>
    /// Separated from the process plumbing so the protocol can be tested against a fake
    /// stdio pair: the request lines, the id matching, the error handling and the empty
    /// response are all exercised without a CLI, a network or an account.
    /// </remarks>
    public static async Task<CodexLiveResult> ExchangeAsync(
        TextWriter requests,
        TextReader responses,
        string clientName,
        string clientVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(responses);

        await WriteLineAsync(requests, InitializeRequest(clientName, clientVersion), ct).ConfigureAwait(false);
        await WriteLineAsync(requests, "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":null}", ct).ConfigureAwait(false);
        await WriteLineAsync(requests, Request(RateLimitsId, "account/rateLimits/read"), ct).ConfigureAwait(false);
        await WriteLineAsync(requests, Request(UsageId, "account/usage/read"), ct).ConfigureAwait(false);

        CodexRateLimitSnapshot? rateLimits = null;
        CodexAccountUsage? usage = null;
        bool sawRateLimitsReply = false;
        bool sawUsageReply = false;
        bool initializeFailed = false;

        for (int line = 0; line < MaxLinesToRead && !(sawRateLimitsReply && sawUsageReply); line++)
        {
            string? text = await responses.ReadLineAsync(ct).ConfigureAwait(false);
            if (text is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text, DocumentOptions);
            }
            catch (JsonException)
            {
                // The app-server writes progress lines that are not always JSON. Skip.
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                long? id = JsonValues.ReadInt64(root, "id");
                if (id is null)
                {
                    // A notification. Not an answer to anything asked here.
                    continue;
                }

                bool isError = root.TryGetProperty("error", out JsonElement error) && error.ValueKind is JsonValueKind.Object;
                bool hasResult = JsonValues.TryGetObject(root, "result", null, out JsonElement result);

                switch (id.Value)
                {
                    case InitializeId:
                        initializeFailed = isError;
                        break;

                    case RateLimitsId:
                        sawRateLimitsReply = true;
                        if (!isError && hasResult)
                        {
                            rateLimits = ReadRateLimits(result);
                        }

                        break;

                    case UsageId:
                        sawUsageReply = true;
                        if (!isError && hasResult)
                        {
                            usage = ReadAccountUsage(result);
                        }

                        break;

                    default:
                        break;
                }
            }
        }

        if (initializeFailed || rateLimits is null)
        {
            // A refusal here is the normal answer under API-key authentication. The caller
            // falls back to the newest local snapshot and says how old it is.
            return usage is null ? CodexLiveResult.Failed : new CodexLiveResult(CodexLiveOutcome.Failed, null, usage);
        }

        return new CodexLiveResult(CodexLiveOutcome.Succeeded, rateLimits, usage);
    }

    /// <summary>
    /// Reads the rate-limit half of the response.
    /// </summary>
    /// <param name="result">The JSON-RPC result object.</param>
    /// <returns>The snapshot, which may legitimately carry no windows.</returns>
    /// <remarks>
    /// A result that reports zero windows is a <b>successful</b> reading that says "no
    /// meters" — it is what an account sees before its first request of a period, and what
    /// a family that stopped reporting a window produces. Treating it as a failure sent the
    /// caller back to a stale local snapshot and put a number on screen that the live source
    /// had just declined to report.
    /// </remarks>
    public static CodexRateLimitSnapshot? ReadRateLimits(in JsonElement result)
    {
        if (result.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        IReadOnlyList<CodexLimitWindow> windows = CodexRateLimitParser.ReadWindows(result);
        CodexRateLimitParser.ReadAccountFields(result, out string? planType, out CodexCredits? credits, out long? resetCredits);

        return new CodexRateLimitSnapshot(
            windows,
            planType,
            credits,
            resetCredits,
            CodexSnapshotSource.Live,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reads the account-usage half of the response.
    /// </summary>
    /// <param name="result">The JSON-RPC result object.</param>
    /// <returns>
    /// The figures, or <see langword="null"/> when nothing recognisable was reported. A
    /// field whose shape is not the one expected reads as unavailable; none of them is
    /// defaulted to a number.
    /// </returns>
    /// <remarks>
    /// The documented shape is <c>summary.{lifetimeTokens, currentStreakDays,
    /// longestStreakDays, peakDailyTokens}</c> with the daily history in
    /// <c>dailyUsageBuckets</c>. Alternate spellings are tolerated because the interface is
    /// experimental, and the whole thing is graded best-effort.
    /// </remarks>
    public static CodexAccountUsage? ReadAccountUsage(in JsonElement result)
    {
        if (result.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        JsonElement summary = JsonValues.TryGetObject(result, "summary", "usageSummary", out JsonElement found)
            ? found
            : result;

        long? lifetime = JsonValues.ReadCount(summary, "lifetimeTokens", "lifetime_tokens");
        long? current = JsonValues.ReadCount(summary, "currentStreakDays", "current_streak_days");
        long? longest = JsonValues.ReadCount(summary, "longestStreakDays", "longest_streak_days");
        long? peak = JsonValues.ReadCount(summary, "peakDailyTokens", "peak_daily_tokens");
        long? buckets = CountArray(result, "dailyUsageBuckets", "daily_usage_buckets");

        var usage = new CodexAccountUsage(lifetime, buckets, current, longest, peak);
        return usage.HasAny ? usage : null;
    }

    /// <inheritdoc />
    public async Task<CodexLiveResult> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (!ExecutableResolver.TryResolve(_command, out string? executable))
        {
            return CodexLiveResult.NotDetected;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            WorkingDirectory = CliRunner.NeutralWorkingDirectory(),
        };
        startInfo.ArgumentList.Add("app-server");

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return CodexLiveResult.Failed;
            }
        }
        catch (Win32Exception ex)
        {
            // Only a file that has gone missing since the resolve means "not installed".
            // A bad image format or a refused execution is a fault, and calling it "not
            // installed" would hide it behind an invitation to install what is already here.
            return ex.NativeErrorCode is 2 or 3 ? CodexLiveResult.NotDetected : CodexLiveResult.Failed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return CodexLiveResult.Failed;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        // Started before the exchange and never awaited on the failure paths: its only job
        // is to keep the child's error pipe from filling.
        Task drain = DrainAsync(process.StandardError, deadline.Token);

        try
        {
            process.StandardInput.AutoFlush = false;
            CodexLiveResult result = await ExchangeAsync(
                process.StandardInput,
                process.StandardOutput,
                _clientName,
                _clientVersion,
                deadline.Token).ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested ? CodexLiveResult.Failed : CodexLiveResult.TimedOut;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or JsonException)
        {
            return CodexLiveResult.Failed;
        }
        finally
        {
            KillTree(process);
            await Observe(drain).ConfigureAwait(false);
        }
    }

    private static long? CountArray(in JsonElement parent, string name, string alternateName)
    {
        if (parent.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        foreach (string candidate in new[] { name, alternateName })
        {
            if (parent.TryGetProperty(candidate, out JsonElement array) && array.ValueKind is JsonValueKind.Array)
            {
                return array.GetArrayLength();
            }
        }

        return null;
    }

    private static string Request(int id, string method) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + JsonValues.FormatInvariant(id) + ",\"method\":\"" + method + "\"}";

    private static async Task DrainAsync(TextReader reader, CancellationToken ct)
    {
        try
        {
            char[] buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closed, or the exchange finished first. Either way there is nothing
            // to report: this task exists only so the child never blocks on a full pipe.
        }
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Already handled inside the drain; observed here so it is never unobserved.
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception or AggregateException)
        {
            // Already gone, or the kill was refused.
        }
    }

    private static async Task WriteLineAsync(TextWriter writer, string payload, CancellationToken ct)
    {
        await writer.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
        await writer.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string InitializeRequest(string clientName, string clientVersion)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", InitializeId);
            writer.WriteString("method", "initialize");
            writer.WriteStartObject("params");
            writer.WriteStartObject("clientInfo");
            writer.WriteString("name", clientName);
            writer.WriteString("version", clientVersion);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
