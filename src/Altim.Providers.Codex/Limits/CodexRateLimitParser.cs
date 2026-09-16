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
/// <c>primary</c> and <c>secondary</c> slots.
/// </para>
/// <para>
/// <c>rateLimitsByLimitId</c> wins whenever it is present, because the top-level object
/// silently picks a family: an account with both <c>codex</c> and <c>codex_bengalfox</c>
/// limits would have one of them rendered as if it were the whole picture.
/// </para>
/// <para>
/// <b>The family is read, never assumed.</b> Both dialects carry <c>limit_id</c> beside the
/// slots — local rollout files on the verification machine carry <c>codex</c> on nineteen
/// recent sessions and <c>premium</c> on five. Defaulting every local snapshot to
/// <c>codex</c> keyed a premium meter's history to the wrong series, so the same limit
/// accumulated two histories and its label changed depending on which source answered.
/// </para>
/// <para>
/// <c>resets_in_seconds</c> is not read. It is legacy, with zero occurrences across 2,517
/// local files, and a relative value is worthless by the time it is displayed.
/// </para>
/// </remarks>
public static class CodexRateLimitParser
{
    /// <summary>
    /// The family name used when a source reports windows without naming a family. It is a
    /// last resort: <c>limit_id</c> is read first wherever it is present.
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
            // One family. It names itself in limit_id; it may also turn out to be a family
            // map rather than a slot object, so both are tried.
            return LooksLikeFamilyMap(single) ? ReadFamilies(single) : ReadSlots(single, FamilyOf(single, null));
        }

        // The caller may already have unwrapped the object.
        return LooksLikeFamilyMap(container)
            ? ReadFamilies(container)
            : ReadSlots(container, FamilyOf(container, null));
    }

    /// <summary>
    /// Reads the plan name, the credit position and the reset-credit count when they are
    /// present.
    /// </summary>
    /// <param name="container">
    /// The object holding the rate limits, or the rate-limits object itself.
    /// </param>
    /// <param name="planType">The plan name, or <see langword="null"/>.</param>
    /// <param name="credits">The credit position, or <see langword="null"/>.</param>
    /// <param name="resetCreditsAvailable">
    /// How many reset credits are available, or <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// <c>planType</c> and <c>credits</c> sit <em>inside</em> the rate-limits object in both
    /// dialects. Reading them from the top level of the response finds nothing, which is
    /// how a plan name and a credit balance that were both present came back null.
    /// <c>rateLimitResetCredits</c> is the exception: it is a top-level summary object whose
    /// <c>availableCount</c> is the number, not a number itself.
    /// </remarks>
    public static void ReadAccountFields(
        in JsonElement container,
        out string? planType,
        out CodexCredits? credits,
        out long? resetCreditsAvailable)
    {
        planType = null;
        credits = null;
        resetCreditsAvailable = null;

        if (container.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonElement scope in AccountScopes(container))
        {
            planType ??= JsonValues.ReadIdentifier(scope, "planType", "plan_type");
            credits ??= ReadCredits(scope);
        }

        resetCreditsAvailable = ReadResetCredits(container);
    }

    /// <summary>
    /// The objects that may carry the plan and credit fields, most specific first: each
    /// reported family, then the single-family object, then the container itself.
    /// </summary>
    private static IEnumerable<JsonElement> AccountScopes(JsonElement container)
    {
        if (JsonValues.TryGetObject(container, "rateLimitsByLimitId", "rate_limits_by_limit_id", out JsonElement byLimitId))
        {
            foreach (JsonProperty family in byLimitId.EnumerateObject())
            {
                if (family.Value.ValueKind is JsonValueKind.Object)
                {
                    yield return family.Value;
                }
            }
        }

        if (JsonValues.TryGetObject(container, "rate_limits", "rateLimits", out JsonElement single))
        {
            yield return single;
        }

        yield return container;
    }

    private static CodexCredits? ReadCredits(in JsonElement scope)
    {
        if (!JsonValues.TryGetObject(scope, "credits", null, out JsonElement credits))
        {
            return null;
        }

        var parsed = new CodexCredits(
            JsonValues.ReadBoolean(credits, "has_credits", "hasCredits"),
            JsonValues.ReadBoolean(credits, "unlimited"),
            JsonValues.ReadLooseDouble(credits, "balance"));

        return parsed.HasAny ? parsed : null;
    }

    private static long? ReadResetCredits(in JsonElement container)
    {
        if (JsonValues.TryGetObject(container, "rateLimitResetCredits", "rate_limit_reset_credits", out JsonElement summary))
        {
            return JsonValues.ReadCount(summary, "availableCount", "available_count");
        }

        // Tolerated rather than expected: an older shape wrote the count directly.
        return JsonValues.ReadCount(container, "rateLimitResetCredits", "rate_limit_reset_credits");
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

            // The map key is the family. The object beneath it repeats the id, and is used
            // when the key is not usable as one.
            string? key = JsonValues.IsIdentifier(family.Name) ? family.Name : null;
            windows.AddRange(ReadSlots(family.Value, FamilyOf(family.Value, key)));
        }

        return windows;
    }

    private static string FamilyOf(in JsonElement family, string? preferred) =>
        preferred
        ?? JsonValues.ReadIdentifier(family, "limit_id", "limitId")
        ?? DefaultLimitId;

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
