using System.Text.Json;
using Altim.Providers.Io;
using Altim.Providers.Limits;

namespace Altim.Providers.Codex.Limits;

/// <summary>
/// Reads quota windows out of either Codex dialect.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes arrive here. The app-server reports <c>rateLimitsByLimitId</c>, an object
/// keyed by limit family, alongside a top-level <c>rateLimits</c> that holds one family
/// and does not say which. Rollout files report the snake-case <c>rate_limits</c> with
/// <c>primary</c> and <c>secondary</c> slots and no family key at all.
/// </para>
/// <para>
/// <c>rateLimitsByLimitId</c> wins whenever it is present, because the top-level object
/// silently picks a family: an account with both <c>codex</c> and <c>codex_bengalfox</c>
/// limits would have one of them rendered as if it were the whole picture.
/// </para>
/// <para>
/// <c>resets_in_seconds</c> is not read. It is legacy, with zero occurrences across 2,517
/// local files, and a relative value is worthless by the time it is displayed.
/// </para>
/// </remarks>
public static class CodexRateLimitParser
{
    /// <summary>
    /// The family name used when a source reports windows without naming a family, which
    /// is what rollout files do.
    /// </summary>
    public const string DefaultLimitId = "codex";

    private static readonly string[] SlotNames = ["primary", "secondary"];

    /// <summary>
    /// Reads every window from an object that may contain either dialect.
    /// </summary>
    /// <param name="container">
    /// The object holding the rate limits: the app-server result, or a rollout
    /// <c>token_count</c> payload.
    /// </param>
    /// <returns>Windows in family order, then slot order. Empty when none are reported.</returns>
    public static IReadOnlyList<CodexLimitWindow> ReadWindows(in JsonElement container)
    {
        if (container.ValueKind is not JsonValueKind.Object)
        {
            return [];
        }

        if (JsonValues.TryGetObject(container, "rateLimitsByLimitId", "rate_limits_by_limit_id", out JsonElement byLimitId))
        {
            return ReadFamilies(byLimitId);
        }

        if (JsonValues.TryGetObject(container, "rate_limits", "rateLimits", out JsonElement single))
        {
            // One unnamed family. It may still turn out to be a family map rather than a
            // slot object, so both are tried.
            return LooksLikeFamilyMap(single) ? ReadFamilies(single) : ReadSlots(single, DefaultLimitId);
        }

        // The caller may already have unwrapped the object.
        return LooksLikeFamilyMap(container) ? ReadFamilies(container) : ReadSlots(container, DefaultLimitId);
    }

    /// <summary>
    /// Reads the plan name, credit balance and reset credits when they are present.
    /// </summary>
    /// <param name="container">The object holding the rate limits.</param>
    /// <param name="planType">The plan name, or <see langword="null"/>.</param>
    /// <param name="credits">The credit balance, or <see langword="null"/>.</param>
    /// <param name="resetCredits">Credits restored at reset, or <see langword="null"/>.</param>
    public static void ReadAccountFields(in JsonElement container, out string? planType, out double? credits, out double? resetCredits)
    {
        planType = JsonValues.ReadIdentifier(container, "planType", "plan_type");
        credits = JsonValues.ReadDouble(container, "credits", "creditBalance");
        resetCredits = JsonValues.ReadDouble(container, "rateLimitResetCredits", "rate_limit_reset_credits");
    }

    private static bool LooksLikeFamilyMap(in JsonElement candidate)
    {
        foreach (JsonProperty property in candidate.EnumerateObject())
        {
            if (SlotNames.Contains(property.Name, StringComparer.Ordinal))
            {
                return false;
            }

            // A family map's values are themselves objects holding slots.
            if (property.Value.ValueKind is JsonValueKind.Object)
            {
                foreach (string slot in SlotNames)
                {
                    if (property.Value.TryGetProperty(slot, out _))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static List<CodexLimitWindow> ReadFamilies(in JsonElement byLimitId)
    {
        var windows = new List<CodexLimitWindow>();

        foreach (JsonProperty family in byLimitId.EnumerateObject())
        {
            if (family.Value.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            string limitId = JsonValues.IsIdentifier(family.Name) ? family.Name : DefaultLimitId;
            windows.AddRange(ReadSlots(family.Value, limitId));
        }

        return windows;
    }

    private static List<CodexLimitWindow> ReadSlots(in JsonElement family, string limitId)
    {
        var windows = new List<CodexLimitWindow>();
        string? familyName = JsonValues.ReadIdentifier(family, "limit_name", "limitName");

        foreach (string slot in SlotNames)
        {
            if (!JsonValues.TryGetObject(family, slot, null, out JsonElement window))
            {
                // A family that stopped reporting a slot reports null there. The meter
                // disappears rather than showing the last number it had.
                continue;
            }

            CodexLimitWindow? parsed = ReadWindow(window, limitId, familyName);
            if (parsed is not null)
            {
                windows.Add(parsed);
            }
        }

        return windows;
    }

    private static CodexLimitWindow? ReadWindow(in JsonElement window, string limitId, string? familyName)
    {
        long? minutes = JsonValues.ReadInt64(window, "window_minutes", "windowDurationMins")
            ?? JsonValues.ReadInt64(window, "windowMinutes", "window_duration_mins");

        if (minutes is not { } windowMinutes || windowMinutes <= 0)
        {
            // Without a length the window cannot be classified, and classifying by slot
            // name is exactly the mistake this parser exists to avoid.
            return null;
        }

        double? percent = PercentReading.Normalize(JsonValues.ReadDouble(window, "used_percent", "usedPercent"));
        DateTimeOffset? resetsAt = JsonValues.ReadUnixSeconds(window, "resets_at", "resetsAt");
        string? name = JsonValues.ReadIdentifier(window, "limit_name", "limitName") ?? familyName;

        return new CodexLimitWindow(limitId, windowMinutes, percent, resetsAt, name);
    }
}
