using System.Text.Json;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Io;

namespace Altim.Providers.Codex.Rollout;

/// <summary>
/// Recovers quota and token figures from the tail of a Codex rollout file.
/// </summary>
/// <remarks>
/// <para>
/// This is the offline fallback: when the live call is skipped, refused or unavailable, the
/// newest rollout still holds the last quota snapshot the CLI received, and stating that
/// snapshot with its age is better than showing nothing.
/// </para>
/// <para>
/// Only the last few kilobytes of each file are read. The store measured 28.3 GB across
/// 2,518 files and the record that matters sits within about 1.2 KB of end-of-file, so a
/// full read would cost four orders of magnitude more I/O for the same answer.
/// </para>
/// <para>
/// Two line schemas are understood. The older one is an <c>event_msg</c> whose payload type
/// is <c>token_count</c>, carrying <c>rate_limits</c> and an <c>info</c> object. The newer
/// one is a <c>token_usage_record</c> carrying <c>usage</c>, <c>turn_token_usage</c> and
/// <c>thread_token_usage</c>. Anything else is skipped, including every line that holds
/// conversation content.
/// </para>
/// <para>
/// Compressed rollouts (<c>.zst</c>) are skipped rather than guessed at. A file that cannot
/// be read contributes nothing; it never contributes an estimate.
/// </para>
/// </remarks>
public static class CodexRolloutReader
{
    /// <summary>
    /// Reads the tail of one rollout file.
    /// </summary>
    /// <param name="path">The rollout file.</param>
    /// <param name="tailBytes">How many bytes to take from the end.</param>
    /// <returns>
    /// The records found, oldest first. Empty for a missing, locked, compressed or
    /// content-only tail.
    /// </returns>
    public static IReadOnlyList<CodexRolloutRecord> ReadTail(string path, int tailBytes = JsonlTailReader.DefaultTailBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return JsonlTailReader.ReadTail<CodexRolloutRecord>(path, TryParseLine, tailBytes).Values;
    }

    /// <summary>
    /// The newest quota snapshot in a set of records.
    /// </summary>
    /// <param name="records">Records from one file, oldest first.</param>
    /// <param name="fallbackObservedAt">
    /// The instant to attribute the snapshot to when no line carried a timestamp, normally
    /// the file's last-write time.
    /// </param>
    /// <returns>
    /// The snapshot, or <see langword="null"/> when no line reported a window.
    /// </returns>
    public static CodexRateLimitSnapshot? LatestSnapshot(IReadOnlyList<CodexRolloutRecord> records, DateTimeOffset? fallbackObservedAt)
    {
        ArgumentNullException.ThrowIfNull(records);

        for (int i = records.Count - 1; i >= 0; i--)
        {
            CodexRolloutRecord record = records[i];
            if (record.Windows.Count == 0)
            {
                continue;
            }

            return new CodexRateLimitSnapshot(
                record.Windows,
                record.PlanType,
                record.Credits,
                ResetCreditsAvailable: null,
                CodexSnapshotSource.LocalSnapshot,
                record.ObservedAt ?? fallbackObservedAt);
        }

        return null;
    }

    /// <summary>
    /// The session's cumulative token counts as of the last line that reported them.
    /// </summary>
    /// <param name="records">Records from one file, oldest first.</param>
    /// <returns>
    /// The counts, or <see langword="null"/> when no line reported any.
    /// </returns>
    /// <remarks>
    /// The last cumulative value is taken once per session and never summed across lines.
    /// Cumulative totals restate the whole session every time they are written, so adding
    /// them together inflates a session's usage by roughly the number of turns in it.
    /// </remarks>
    public static CodexTokenCounts? LatestCumulativeTokens(IReadOnlyList<CodexRolloutRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        for (int i = records.Count - 1; i >= 0; i--)
        {
            if (records[i].CumulativeTokens is { HasAny: true } counts)
            {
                return counts;
            }
        }

        return null;
    }

    private static bool TryParseLine(ReadOnlyMemory<byte> utf8Line, out CodexRolloutRecord record)
    {
        record = default!;

        using JsonDocument? document = JsonlTailReader.ParseObject(utf8Line);
        if (document is null)
        {
            return false;
        }

        JsonElement root = document.RootElement;
        string? type = JsonValues.ReadIdentifier(root, "type");
        if (type is null)
        {
            return false;
        }

        if (!JsonValues.TryGetObject(root, "payload", null, out JsonElement payload))
        {
            return false;
        }

        DateTimeOffset? timestamp = JsonValues.ReadIso8601(root, "timestamp")
            ?? JsonValues.ReadIso8601(payload, "timestamp");

        if (string.Equals(type, "event_msg", StringComparison.Ordinal))
        {
            return TryParseTokenCount(payload, timestamp, out record);
        }

        if (string.Equals(type, "token_usage_record", StringComparison.Ordinal))
        {
            return TryParseUsageRecord(payload, timestamp, out record);
        }

        return false;
    }

    private static bool TryParseTokenCount(in JsonElement payload, DateTimeOffset? timestamp, out CodexRolloutRecord record)
    {
        record = default!;

        string? payloadType = JsonValues.ReadIdentifier(payload, "type");
        if (!string.Equals(payloadType, "token_count", StringComparison.Ordinal))
        {
            return false;
        }

        IReadOnlyList<CodexLimitWindow> windows = CodexRateLimitParser.ReadWindows(payload);
        CodexRateLimitParser.ReadAccountFields(payload, out string? planType, out CodexCredits? credits, out _);

        CodexTokenCounts? cumulative = null;
        CodexTokenCounts? turn = null;
        long? contextWindow = null;
        string? modelId = null;

        if (JsonValues.TryGetObject(payload, "info", null, out JsonElement info))
        {
            contextWindow = JsonValues.ReadCount(info, "model_context_window", "modelContextWindow");
            modelId = JsonValues.ReadIdentifier(info, "model", "model_id");

            if (JsonValues.TryGetObject(info, "total_token_usage", "totalTokenUsage", out JsonElement total))
            {
                cumulative = ReadCounts(total);
            }

            if (JsonValues.TryGetObject(info, "last_token_usage", "lastTokenUsage", out JsonElement last))
            {
                turn = ReadCounts(last);
            }
        }

        if (windows.Count == 0 && cumulative is null && turn is null && contextWindow is null)
        {
            return false;
        }

        record = new CodexRolloutRecord(timestamp, windows, cumulative, turn, contextWindow, modelId, planType, credits);
        return true;
    }

    private static bool TryParseUsageRecord(in JsonElement payload, DateTimeOffset? timestamp, out CodexRolloutRecord record)
    {
        record = default!;

        CodexTokenCounts? cumulative = null;
        CodexTokenCounts? turn = null;

        if (JsonValues.TryGetObject(payload, "thread_token_usage", "threadTokenUsage", out JsonElement thread))
        {
            cumulative = ReadCounts(thread);
        }

        if (JsonValues.TryGetObject(payload, "turn_token_usage", "turnTokenUsage", out JsonElement turnUsage))
        {
            turn = ReadCounts(turnUsage);
        }
        else if (JsonValues.TryGetObject(payload, "usage", null, out JsonElement usage))
        {
            turn = ReadCounts(usage);
        }

        IReadOnlyList<CodexLimitWindow> windows = CodexRateLimitParser.ReadWindows(payload);
        CodexRateLimitParser.ReadAccountFields(payload, out string? planType, out CodexCredits? credits, out _);
        string? modelId = JsonValues.ReadIdentifier(payload, "model", "model_id");
        long? contextWindow = JsonValues.ReadCount(payload, "model_context_window", "modelContextWindow");

        if (windows.Count == 0 && cumulative is null && turn is null)
        {
            return false;
        }

        record = new CodexRolloutRecord(timestamp, windows, cumulative, turn, contextWindow, modelId, planType, credits);
        return true;
    }

    /// <summary>
    /// Reads one token-usage object.
    /// </summary>
    /// <remarks>
    /// Every component is optional and stays null when it is absent. In particular
    /// <c>cache_write_input_tokens</c> is part of the real schema — it is written on every
    /// <c>token_count</c> line on the verification machine and is declared on the
    /// app-server's own token breakdown — so it is read rather than assumed away.
    /// </remarks>
    internal static CodexTokenCounts ReadCounts(in JsonElement usage) => new(
        JsonValues.ReadCount(usage, "input_tokens", "inputTokens"),
        JsonValues.ReadCount(usage, "cached_input_tokens", "cachedInputTokens"),
        JsonValues.ReadCount(usage, "cache_write_input_tokens", "cacheWriteInputTokens"),
        JsonValues.ReadCount(usage, "output_tokens", "outputTokens"),
        JsonValues.ReadCount(usage, "reasoning_output_tokens", "reasoningOutputTokens"),
        JsonValues.ReadCount(usage, "total_tokens", "totalTokens"));
}
