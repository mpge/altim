using System.Text;
using System.Text.Json;
using Altim.Providers.Io;
using Altim.Providers.Limits;

namespace Altim.Providers.Claude.StatusLine;

/// <summary>
/// Reads the status-line state file Altim's helper writes.
/// </summary>
/// <remarks>
/// <para>
/// The file is the documented status-line payload with everything non-numeric dropped, so
/// the same parser reads either. Documented property names are used
/// (<c>rate_limits.five_hour.used_percentage</c>, <c>cost.total_cost_usd</c>,
/// <c>context_window.*</c>, <c>prompt_cache.*</c>), and each one is optional.
/// </para>
/// <para>
/// A known defect returns an epoch timestamp in place of a percentage before a window has
/// data. Anything above the plausible ceiling is discarded here, so it never reaches a
/// meter or a notification threshold.
/// </para>
/// </remarks>
public static class ClaudeStatusLineReader
{
    private const int MaxStateFileBytes = 256 * 1024;

    /// <summary>
    /// Reads the state file.
    /// </summary>
    /// <param name="path">The state file.</param>
    /// <returns>
    /// The state, or <see langword="null"/> when the file is missing, unreadable, too large
    /// or not JSON. A missing file means the status line is not installed or no session has
    /// run, both of which are ordinary.
    /// </returns>
    public static ClaudeStatusLineState? Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        byte[] bytes;
        DateTimeOffset? lastWrite;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxStateFileBytes)
            {
                return null;
            }

            lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        return Parse(bytes, lastWrite);
    }

    /// <summary>
    /// Parses a status-line payload.
    /// </summary>
    /// <param name="json">The payload.</param>
    /// <param name="fallbackWrittenAt">
    /// The instant to attribute the reading to when the payload does not carry one.
    /// </param>
    /// <returns>The state, or <see langword="null"/> when the payload is not a JSON object.</returns>
    public static ClaudeStatusLineState? Parse(string json, DateTimeOffset? fallbackWrittenAt = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json), fallbackWrittenAt);
    }

    private static ClaudeStatusLineState? Parse(byte[] utf8Json, DateTimeOffset? fallbackWrittenAt)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            DateTimeOffset? writtenAt = JsonValues.ReadUnixSeconds(root, "written_at", "writtenAt")
                ?? JsonValues.ReadIso8601(root, "written_at", "writtenAt")
                ?? fallbackWrittenAt;

            double? fiveHour = null;
            DateTimeOffset? fiveHourResets = null;
            double? sevenDay = null;
            DateTimeOffset? sevenDayResets = null;
            double? spend = null;
            DateTimeOffset? spendResets = null;

            if (JsonValues.TryGetObject(root, "rate_limits", "rateLimits", out JsonElement limits))
            {
                ReadWindow(limits, "five_hour", "fiveHour", out fiveHour, out fiveHourResets);
                ReadWindow(limits, "seven_day", "sevenDay", out sevenDay, out sevenDayResets);
                ReadWindow(limits, "spend_limit", "spendLimit", out spend, out spendResets);
            }

            double? cost = null;
            if (JsonValues.TryGetObject(root, "cost", null, out JsonElement costElement))
            {
                cost = JsonValues.ReadDouble(costElement, "total_cost_usd", "totalCostUsd");
            }

            long? contextUsed = null;
            long? contextMax = null;
            if (JsonValues.TryGetObject(root, "context_window", "contextWindow", out JsonElement context))
            {
                contextUsed = JsonValues.ReadCount(context, "used_tokens", "usedTokens")
                    ?? JsonValues.ReadCount(context, "input_tokens", "inputTokens");
                contextMax = JsonValues.ReadCount(context, "max_tokens", "maxTokens")
                    ?? JsonValues.ReadCount(context, "size", "context_size");
            }

            long? cacheRead = null;
            long? cacheCreation = null;
            if (JsonValues.TryGetObject(root, "prompt_cache", "promptCache", out JsonElement cache))
            {
                cacheRead = JsonValues.ReadCount(cache, "cache_read_input_tokens", "cacheReadInputTokens")
                    ?? JsonValues.ReadCount(cache, "read_tokens", "readTokens");
                cacheCreation = JsonValues.ReadCount(cache, "cache_creation_input_tokens", "cacheCreationInputTokens")
                    ?? JsonValues.ReadCount(cache, "creation_tokens", "creationTokens");
            }

            string? modelId = JsonValues.ReadIdentifier(root, "model_id", "modelId");
            if (modelId is null && JsonValues.TryGetObject(root, "model", null, out JsonElement model))
            {
                modelId = JsonValues.ReadIdentifier(model, "id", "model_id");
            }

            string? sessionId = JsonValues.ReadIdentifier(root, "session_id", "sessionId");

            return new ClaudeStatusLineState(
                writtenAt,
                fiveHour,
                fiveHourResets,
                sevenDay,
                sevenDayResets,
                spend,
                spendResets,
                cost,
                contextUsed,
                contextMax,
                cacheRead,
                cacheCreation,
                modelId,
                sessionId);
        }
    }

    private static void ReadWindow(in JsonElement limits, string name, string alternateName, out double? percent, out DateTimeOffset? resetsAt)
    {
        if (!JsonValues.TryGetObject(limits, name, alternateName, out JsonElement window))
        {
            // Dropped from the payload once its reset passed. No data, not zero.
            percent = null;
            resetsAt = null;
            return;
        }

        percent = PercentReading.Normalize(JsonValues.ReadDouble(window, "used_percentage", "usedPercentage"));
        resetsAt = JsonValues.ReadUnixSeconds(window, "resets_at", "resetsAt");
    }
}
