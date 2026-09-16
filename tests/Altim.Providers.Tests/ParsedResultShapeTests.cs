using System.Reflection;
using Altim.Core.Models;
using Altim.Providers.Claude.Sessions;
using Altim.Providers.Claude.StatusLine;
using Altim.Providers.Claude.Transcripts;
using Altim.Providers.Claude.Usage;
using Altim.Providers.Codex.AppServer;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Codex.Rollout;
using Altim.Providers.Codex.State;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The privacy guarantee, asserted against the type system rather than against intent.
/// </summary>
/// <remarks>
/// <para>
/// Every type a provider reader hands out is listed here, and every property on each of them
/// must be a number, a boolean, an instant, a duration, an enum, another listed type, or a
/// string whose name is on a short allowlist of identifiers. Collections are unwrapped and
/// checked the same way.
/// </para>
/// <para>
/// The point is that this fails when someone adds a convenient <c>string? ProjectPath</c> or
/// <c>string? LastMessage</c> to a record, which is exactly how a privacy promise erodes: not
/// by a decision, but by a field. Reviewers forget; a test does not.
/// </para>
/// </remarks>
public sealed class ParsedResultShapeTests
{
    /// <summary>Everything a reader in this solution returns.</summary>
    private static readonly Type[] Types =
    [
        typeof(ProviderUsage),
        typeof(UsageMetric),
        typeof(LimitWindow),
        typeof(TokenTotals),
        typeof(AgentSession),
        typeof(DetectedProcess),
        typeof(UsageSample),
        typeof(CodexLimitWindow),
        typeof(CodexRateLimitSnapshot),
        typeof(CodexCredits),
        typeof(CodexRolloutRecord),
        typeof(CodexTokenCounts),
        typeof(CodexAccountUsage),
        typeof(CodexLiveResult),
        typeof(CodexThreadSummary),
        typeof(ClaudeStatusLineState),
        typeof(ClaudeUsageLine),
        typeof(ClaudeTokenBucket),
        typeof(ClaudeTokenHistory),
        typeof(ClaudeAgentEntry),
        typeof(ClaudeUsageSummary),
    ];

    /// <summary>
    /// The property names allowed to be strings. Each is an opaque identifier, a stable
    /// storage key, or a label Altim itself authored; none can hold provider content.
    /// </summary>
    private static readonly HashSet<string> IdentifierProperties = new(StringComparer.Ordinal)
    {
        "Id", "Identity", "ProviderId", "SessionId", "MessageId", "RequestId", "ModelId",
        "ThreadId", "LimitId", "LimitName", "PlanType", "Kind", "Status", "ExecutableName",
        "Key", "Label", "StatusDetail", "MetricKey",
    };

    private static readonly string[] ContentBearingWords =
    [
        "content", "text", "prompt", "body", "path", "cwd", "directory", "folder", "file",
        "url", "uri", "repo", "project", "argument", "commandline", "argv", "instruction",
        "secret", "credential", "apikey", "email", "summary", "detailtext",
    ];

    public static TheoryData<Type> ParsedResultTypes => [.. Types];

    [Theory]
    [MemberData(nameof(ParsedResultTypes))]
    public void EveryPropertyIsANumberAnInstantOrAnAllowlistedIdentifier(Type type)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(
                IsAllowed(property.PropertyType, property.Name),
                type.Name + "." + property.Name + " is a " + property.PropertyType.Name
                + ", which is not a number, instant, enum, listed record or allowlisted identifier. "
                + "A parsed result must not be able to carry provider content.");
        }
    }

    [Theory]
    [MemberData(nameof(ParsedResultTypes))]
    public void NoStringPropertyIsNamedAfterSomethingContentBearing(Type type)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Unwrap(property.PropertyType) != typeof(string))
            {
                continue;
            }

            string lowered = property.Name.ToLowerInvariant();
            foreach (string word in ContentBearingWords)
            {
                Assert.False(
                    lowered.Contains(word, StringComparison.Ordinal),
                    type.Name + "." + property.Name + " reads like a content-bearing field.");
            }
        }
    }

    [Fact]
    public void EveryListedTypeActuallyHasProperties()
    {
        // Guards the guard: a typo that listed an empty marker type would make the sweep
        // above pass vacuously.
        foreach (Type type in Types)
        {
            Assert.NotEmpty(type.GetProperties(BindingFlags.Public | BindingFlags.Instance));
        }
    }

    [Fact]
    public void TheAllowlistIsNotAWildcard()
    {
        Assert.DoesNotContain("Cwd", IdentifierProperties);
        Assert.DoesNotContain("Path", IdentifierProperties);
        Assert.DoesNotContain("Content", IdentifierProperties);
        Assert.DoesNotContain("Command", IdentifierProperties);
    }

    [Fact]
    public void TheRolloutPathStaysInsideTheCodexReader()
    {
        // The state database does read each thread's rollout path — it is how the fallback
        // knows which few files to tail — but the type carrying it is not public, so it
        // cannot reach a view model or a database row.
        Type? row = typeof(CodexStateDatabase).Assembly.GetType("Altim.Providers.Codex.State.CodexThreadRow");

        Assert.NotNull(row);
        Assert.False(row.IsPublic, "CodexThreadRow carries a filesystem path and must stay internal.");
        Assert.DoesNotContain(
            typeof(CodexThreadSummary).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static p => p.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AStatusLineStateCannotCarryTheWorkingDirectoryItWasGiven()
    {
        // The real status-line payload carries cwd, project, the model's display name and the
        // transcript path. Parsing one must leave every one of them behind.
        ClaudeStatusLineState? state = ClaudeStatusLineReader.Parse(
            """
            {
              "cwd": "C:\\work\\private-project",
              "workspace": { "current_dir": "C:\\work\\private-project", "project_dir": "C:\\work" },
              "transcript_path": "C:\\Users\\someone\\.claude\\projects\\slug\\session.jsonl",
              "model": { "id": "claude-opus-4-5-20260101", "display_name": "Opus 4.5" },
              "rate_limits": { "five_hour": { "used_percentage": 53, "resets_at": 1789515600 } }
            }
            """);

        Assert.NotNull(state);
        Assert.Equal("claude-opus-4-5-20260101", state.ModelId);

        foreach (PropertyInfo property in typeof(ClaudeStatusLineState).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetValue(state) is string value)
            {
                Assert.DoesNotContain("private-project", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("\\", value, StringComparison.Ordinal);
                Assert.DoesNotContain("/", value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ATranscriptLineCannotCarryItsMessageContent()
    {
        const string Line =
            """
            {"type":"assistant","sessionId":"0199aaaa","requestId":"req_1","cwd":"C:\\work\\private-project","message":{"id":"msg_1","model":"claude-opus-4-5-20260101","content":[{"type":"text","text":"a sentence that must not survive the parse"}],"usage":{"input_tokens":1,"output_tokens":2,"cache_read_input_tokens":3,"cache_creation":{"ephemeral_5m_input_tokens":4,"ephemeral_1h_input_tokens":5}}}}
            """;

        Assert.True(ClaudeTranscriptScanner.TryParseLine(System.Text.Encoding.UTF8.GetBytes(Line), out ClaudeUsageLine parsed));

        foreach (PropertyInfo property in typeof(ClaudeUsageLine).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetValue(parsed) is string value)
            {
                Assert.DoesNotContain("sentence", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("private-project", value, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Equal("msg_1", parsed.MessageId);
        Assert.Equal(2L, parsed.OutputTokens);
    }

    private static bool IsAllowed(Type type, string propertyName)
    {
        Type unwrapped = Unwrap(type);

        if (IsNumericLike(unwrapped))
        {
            return true;
        }

        if (unwrapped == typeof(string))
        {
            return IdentifierProperties.Contains(propertyName);
        }

        if (Array.IndexOf(Types, unwrapped) >= 0)
        {
            return true;
        }

        if (unwrapped.IsGenericType)
        {
            Type definition = unwrapped.GetGenericTypeDefinition();
            Type[] arguments = unwrapped.GetGenericArguments();

            if (definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>) || definition == typeof(IEnumerable<>))
            {
                return IsAllowed(arguments[0], propertyName);
            }

            if (definition == typeof(IReadOnlyDictionary<,>))
            {
                // A key is an identifier by construction: nothing writes a path into one.
                return arguments[0] == typeof(string) && IsAllowed(arguments[1], propertyName);
            }
        }

        return false;
    }

    private static bool IsNumericLike(Type type) =>
        type.IsEnum
        || type == typeof(bool)
        || type == typeof(byte)
        || type == typeof(short)
        || type == typeof(int)
        || type == typeof(long)
        || type == typeof(float)
        || type == typeof(double)
        || type == typeof(decimal)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateTime)
        || type == typeof(TimeSpan);

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;
}
