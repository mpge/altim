using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Altim.Providers.Cli;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Codex.Rollout;
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
/// </remarks>
public sealed class CodexAppServerClient : ICodexAppServerClient
{
    private const string DefaultCommand = "codex";
    private const int InitializeId = 1;
    private const int RateLimitsId = 2;
    private const int UsageId = 3;
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
        catch (Win32Exception)
        {
            return CodexLiveResult.NotDetected;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return CodexLiveResult.Failed;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            return await ExchangeAsync(process, deadline.Token).ConfigureAwait(false);
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
        }
    }

    private static string Request(int id, string method) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + JsonValues.FormatInvariant(id) + ",\"method\":\"" + method + "\"}";

    private static CodexRateLimitSnapshot? ReadRateLimits(in JsonElement result)
    {
        IReadOnlyList<CodexLimitWindow> windows = CodexRateLimitParser.ReadWindows(result);
        CodexRateLimitParser.ReadAccountFields(result, out string? planType, out double? credits, out double? resetCredits);

        if (windows.Count == 0 && planType is null && credits is null)
        {
            return null;
        }

        return new CodexRateLimitSnapshot(
            windows,
            planType,
            credits,
            resetCredits,
            CodexSnapshotSource.Live,
            DateTimeOffset.UtcNow);
    }

    private static CodexAccountUsage? ReadAccountUsage(in JsonElement result)
    {
        CodexTokenCounts? lifetime = null;
        foreach (string name in new[] { "lifetime", "total", "totals", "allTime", "all_time" })
        {
            if (JsonValues.TryGetObject(result, name, null, out JsonElement totals))
            {
                lifetime = ReadCounts(totals);
                break;
            }
        }

        lifetime ??= ReadCountsOrNull(result);

        long? buckets = null;
        foreach (string name in new[] { "days", "daily", "buckets", "dailyUsage", "daily_usage" })
        {
            if (result.ValueKind is JsonValueKind.Object
                && result.TryGetProperty(name, out JsonElement array)
                && array.ValueKind is JsonValueKind.Array)
            {
                buckets = array.GetArrayLength();
                break;
            }
        }

        long? current = JsonValues.ReadCount(result, "currentStreak", "current_streak")
            ?? JsonValues.ReadCount(result, "streak", "streakDays");
        long? longest = JsonValues.ReadCount(result, "longestStreak", "longest_streak");

        var usage = new CodexAccountUsage(lifetime, buckets, current, longest);
        return usage.HasAny ? usage : null;
    }

    private static CodexTokenCounts? ReadCountsOrNull(in JsonElement element)
    {
        CodexTokenCounts counts = ReadCounts(element);
        return counts.HasAny ? counts : null;
    }

    private static CodexTokenCounts ReadCounts(in JsonElement usage) => new(
        JsonValues.ReadCount(usage, "input_tokens", "inputTokens"),
        JsonValues.ReadCount(usage, "cached_input_tokens", "cachedInputTokens"),
        JsonValues.ReadCount(usage, "output_tokens", "outputTokens"),
        JsonValues.ReadCount(usage, "reasoning_output_tokens", "reasoningOutputTokens"),
        JsonValues.ReadCount(usage, "total_tokens", "totalTokens"));

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

    private async Task<CodexLiveResult> ExchangeAsync(Process process, CancellationToken ct)
    {
        StreamWriter input = process.StandardInput;
        input.AutoFlush = false;

        await WriteLineAsync(input, InitializeRequest(), ct).ConfigureAwait(false);
        await WriteLineAsync(input, "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":null}", ct).ConfigureAwait(false);
        await WriteLineAsync(input, Request(RateLimitsId, "account/rateLimits/read"), ct).ConfigureAwait(false);
        await WriteLineAsync(input, Request(UsageId, "account/usage/read"), ct).ConfigureAwait(false);

        CodexRateLimitSnapshot? rateLimits = null;
        CodexAccountUsage? usage = null;
        bool sawRateLimitsReply = false;
        bool sawUsageReply = false;
        bool initializeFailed = false;

        StreamReader output = process.StandardOutput;
        for (int line = 0; line < MaxLinesToRead && !(sawRateLimitsReply && sawUsageReply); line++)
        {
            string? text = await output.ReadLineAsync(ct).ConfigureAwait(false);
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

    private static async Task WriteLineAsync(StreamWriter writer, string payload, CancellationToken ct)
    {
        await writer.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
        await writer.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private string InitializeRequest()
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
            writer.WriteString("name", _clientName);
            writer.WriteString("version", _clientVersion);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
