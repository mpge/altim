using System.Globalization;
using Altim.Core.Models;
using Altim.Providers.Gemini;
using Altim.Providers.Gemini.Sessions;
using Altim.Providers.Tests.Support;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// What the Gemini CLI reader does and, more importantly, what it refuses to do: it never
/// publishes a percentage, a window or a reset instant, because nothing on this machine
/// reports one.
/// </summary>
/// <remarks>
/// Every fixture here is synthetic and written at run time by <see cref="TempWorkspace"/>.
/// A real Gemini session file carries prompts, model reasoning, tool arguments, tool output
/// and the absolute paths of the session's workspace directories, and the real store sits
/// beside the user's OAuth credentials. Nothing from either is copied into this repository,
/// and no test here points at the real one.
/// </remarks>
public sealed class GeminiUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 16, 0, 0, TimeSpan.Zero);

    private const string MainSessionId = "0199aaaa-bbbb-cccc-dddd-eeeeffff0000";
    private const string SubagentParentId = "0199aaaa-bbbb-cccc-dddd-eeeeffff0000";
    private const string SubagentSessionId = "0199bbbb-cccc-dddd-eeee-ffff00001111";

    private const string ChatsDirectory = "tmp/my-secret-project/chats";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A session file's first line. It carries the project hash and the session's workspace
    /// directories on purpose: both are real fields of the real format, and neither may
    /// survive the parse.
    /// </summary>
    private static string Metadata(string sessionId, DateTimeOffset startedAt, string kind = "main") =>
        "{\"sessionId\":\"" + sessionId + "\""
        + ",\"projectHash\":\"my-secret-project\""
        + ",\"startTime\":\"" + Stamp(startedAt) + "\""
        + ",\"lastUpdated\":\"" + Stamp(startedAt) + "\""
        + ",\"kind\":\"" + kind + "\""
        + ",\"directories\":[\"C:\\\\work\\\\private-project\"]}";

    /// <summary>A user turn. It has no token counts and never will; it is here to be ignored.</summary>
    private static string UserTurn(string messageId, DateTimeOffset at) =>
        "{\"id\":\"" + messageId + "\",\"timestamp\":\"" + Stamp(at) + "\",\"type\":\"user\""
        + ",\"content\":[{\"text\":\"a prompt the reader must not carry\"}]}";

    /// <summary>
    /// A model turn. <paramref name="withTokens"/> false is the first copy the recorder
    /// writes, before the response's usage figures have arrived.
    /// </summary>
    private static string ModelTurn(
        string messageId,
        DateTimeOffset? at,
        bool withTokens = true,
        long input = 1000,
        long output = 200,
        long cached = 400,
        long thoughts = 50,
        long tool = 10,
        long? total = null,
        string model = "gemini-3-pro")
    {
        string timestamp = at is { } instant ? ",\"timestamp\":\"" + Stamp(instant) + "\"" : string.Empty;
        string tokens = withTokens
            ? ",\"tokens\":{\"input\":" + N(input)
                + ",\"output\":" + N(output)
                + ",\"cached\":" + N(cached)
                + ",\"thoughts\":" + N(thoughts)
                + ",\"tool\":" + N(tool)
                + ",\"total\":" + N(total ?? (input + output + thoughts)) + "}"
            : ",\"tokens\":null";

        return "{\"id\":\"" + messageId + "\""
            + timestamp
            + ",\"type\":\"gemini\""
            + ",\"model\":\"" + model + "\""
            + ",\"content\":[{\"text\":\"a sentence the reader must not carry\"}]"
            + ",\"thoughts\":[{\"subject\":\"reasoning the reader must not carry\",\"timestamp\":\"" + Stamp(at ?? Now) + "\"}]"
            + tokens + "}";
    }

    /// <summary>
    /// A Gemini home with the decoys the real one has: credentials, an account cache, the
    /// project registry and a project-root marker holding an absolute path. Nothing may open
    /// any of them.
    /// </summary>
    private static TempWorkspace CreateHome()
    {
        var workspace = new TempWorkspace();
        _ = workspace.Write("oauth_creds.json", "{\"refresh_token\":\"must-never-be-read\"}");
        _ = workspace.Write("google_accounts.json", "{\"active\":\"someone@example.com\"}");
        _ = workspace.Write("mcp-oauth-tokens-v2.json", "{\"token\":\"must-never-be-read\"}");
        _ = workspace.Write("settings.json", "{\"security\":{\"auth\":{\"selectedType\":\"oauth-personal\"}}}");
        _ = workspace.Write("projects.json", "{\"projects\":{\"C:\\\\work\\\\private-project\":\"my-secret-project\"}}");
        _ = workspace.Write("tmp/my-secret-project/.project_root", "C:\\work\\private-project");
        _ = workspace.Write("tmp/my-secret-project/logs.json", "[{\"sessionId\":\"x\",\"message\":\"a prompt\"}]");
        return workspace;
    }

    private static string WriteMainSession(TempWorkspace workspace, params string[] lines) =>
        workspace.WriteLines(ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl", lines);

    private static GeminiUsageProvider CreateProvider(
        string? home,
        bool cliPresent = true,
        GeminiOptions? options = null,
        FakeProcessMonitor? processes = null,
        TimeProvider? clock = null) =>
        new(
            options ?? GeminiOptions.Default,
            new FakeCliRunner { CommandExists = cliPresent },
            processes ?? new FakeProcessMonitor(),
            home,
            clock ?? new FixedTimeProvider(Now));

    [Fact]
    public async Task ReportsNotDetectedWhenNeitherTheCliNorTheHomeExists()
    {
        using var workspace = new TempWorkspace();
        using GeminiUsageProvider provider = CreateProvider(
            Path.Combine(workspace.Root, "no-such-home"),
            cliPresent: false);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        Assert.Equal(ProviderStatus.NotDetected, usage.Status);
        Assert.Empty(usage.Metrics);
        Assert.Null(usage.Tokens);
        Assert.NotNull(usage.StatusDetail);
        Assert.Empty(await provider.GetSessionsAsync(Ct));
    }

    [Fact]
    public async Task AnInstalledCliWithNoStoreIsDetectedAndSaysSo()
    {
        using var workspace = new TempWorkspace();
        using GeminiUsageProvider provider = CreateProvider(
            Path.Combine(workspace.Root, "no-such-home"),
            cliPresent: true);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        Assert.Equal(ProviderStatus.Detected, usage.Status);
        Assert.Empty(usage.Metrics);
        Assert.Null(usage.Tokens);
        Assert.NotNull(usage.StatusDetail);
        Assert.Contains("no session store", usage.StatusDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state this machine is actually in: a store left behind by a CLI that is no longer
    /// on the path. It is still read, and the note still says the meters are absent for the
    /// provider's own reasons rather than because the CLI went missing.
    /// </summary>
    [Fact]
    public async Task AStoreLeftBehindByAnUninstalledCliIsStillRead()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddHours(-2)),
            ModelTurn("m1", Now.AddHours(-2)));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root, cliPresent: false);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        Assert.Equal(ProviderStatus.Idle, usage.Status);
        Assert.Equal(600L, usage.Tokens?.Input);
        Assert.Empty(usage.Metrics);
        Assert.NotNull(usage.StatusDetail);
        Assert.Contains("never quota", usage.StatusDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A store whose newest file is older than the scan window contributes nothing, which is
    /// the state of the dormant installation on the verification machine. Nothing is invented
    /// to fill it: the provider is detected, its tokens are unreported, and its days stay
    /// unknown.
    /// </summary>
    [Fact]
    public async Task AStoreOlderThanTheScanWindowContributesNothing()
    {
        using TempWorkspace workspace = CreateHome();
        string path = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddDays(-240)),
            ModelTurn("m1", Now.AddDays(-240)));

        File.SetLastWriteTimeUtc(path, Now.AddDays(-240).UtcDateTime);

        using GeminiUsageProvider provider = CreateProvider(workspace.Root, cliPresent: false);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        Assert.Equal(ProviderStatus.Detected, usage.Status);
        Assert.Null(usage.Tokens);
        Assert.Empty(usage.Metrics);
        Assert.Empty(await provider.GetHistoryAsync(new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31), Ct));
    }

    /// <summary>
    /// The rule this whole provider exists to demonstrate. A store full of real token counts
    /// still produces no meter, because a meter needs a percentage and Gemini CLI writes none
    /// to disk. The interface renders an absent metric as "not reported by this provider".
    /// </summary>
    [Fact]
    public async Task NeverPublishesAPercentageAWindowOrAResetInstant()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddHours(-2)),
            UserTurn("u1", Now.AddHours(-2)),
            ModelTurn("m1", Now.AddHours(-2)));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        Assert.Empty(usage.Metrics);
        Assert.NotNull(usage.Tokens);
        Assert.NotNull(usage.StatusDetail);
        Assert.Contains("does not report quota", usage.StatusDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one piece of arithmetic in the reader, checked against the provider's own total.
    /// Input is the uncached part of the prompt, cache-read is the cached part, and output is
    /// the response plus its reasoning, so the three sum to what Google documents
    /// <c>totalTokenCount</c> to be.
    /// </summary>
    [Fact]
    public async Task TokensAreTheModelTurnsOwnCountsAndSumToTheProvidersTotal()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddHours(-2)),
            ModelTurn("m1", Now.AddHours(-2), input: 1000, output: 200, cached: 400, thoughts: 50, tool: 10));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        TokenTotals tokens = Assert.IsType<TokenTotals>(usage.Tokens);
        Assert.Equal(600L, tokens.Input);
        Assert.Equal(250L, tokens.Output);
        Assert.Equal(400L, tokens.CacheRead);

        // Not zero. Gemini's response usage says how much of a prompt was served from cache
        // and never how much was written into one, so this is unreported.
        Assert.Null(tokens.CacheWrite);

        // 1000 + 200 + 50, which is the total the fixture's own line reported.
        Assert.Equal(1250L, tokens.Input + tokens.Output + tokens.CacheRead);
    }

    /// <summary>
    /// Cached prompt tokens are inside <c>promptTokenCount</c>, so a reader that added the
    /// two fields would count them twice. Here every prompt token was cached.
    /// </summary>
    [Fact]
    public async Task CachedPromptTokensAreNotCountedTwice()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddHours(-1)),
            ModelTurn("m1", Now.AddHours(-1), input: 900, output: 100, cached: 900, thoughts: 0, tool: 0, total: 1000));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        TokenTotals tokens = Assert.IsType<TokenTotals>(usage.Tokens);
        Assert.Equal(0L, tokens.Input);
        Assert.Equal(900L, tokens.CacheRead);
        Assert.Equal(1000L, tokens.Input + tokens.Output + tokens.CacheRead);
    }

    /// <summary>
    /// The recorder appends the same message again when its usage figures arrive, and again
    /// when its tool calls are enriched. Summing lines instead of distinct ids triples this
    /// fixture.
    /// </summary>
    [Fact]
    public async Task ARepeatedMessageIdIsCountedOnce()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddHours(-1)),
            ModelTurn("m1", Now.AddHours(-1), withTokens: false),
            ModelTurn("m1", Now.AddHours(-1)),
            ModelTurn("m1", Now.AddHours(-1)),
            ModelTurn("m1", Now.AddHours(-1)));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        TokenTotals tokens = Assert.IsType<TokenTotals>(usage.Tokens);
        Assert.Equal(600L, tokens.Input);
        Assert.Equal(250L, tokens.Output);
        Assert.Equal(400L, tokens.CacheRead);
    }

    /// <summary>
    /// The incremental boundary case: an id first seen in one pass must still be known in the
    /// next, or the scanner reintroduces the double count it was written to prevent.
    /// </summary>
    [Fact]
    public async Task AnIdSeenInAnEarlierPassIsStillRememberedInALaterOne()
    {
        using TempWorkspace workspace = CreateHome();
        string path = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddHours(-1)),
            ModelTurn("m1", Now.AddHours(-1)));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);
        _ = await provider.GetUsageAsync(Ct);

        workspace.Append(path, ModelTurn("m1", Now.AddHours(-1)) + "\n" + ModelTurn("m2", Now.AddMinutes(-30)) + "\n");
        await provider.RefreshAsync(Ct);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);
        TokenTotals tokens = Assert.IsType<TokenTotals>(usage.Tokens);

        // Two distinct messages, not three lines.
        Assert.Equal(1200L, tokens.Input);
        Assert.Equal(500L, tokens.Output);
        Assert.Equal(800L, tokens.CacheRead);
    }

    /// <summary>
    /// Subagent transcripts sit one directory deeper and are named after the parent session
    /// rather than with the <c>session-</c> prefix. On the other transcript-based provider
    /// they carried most of the volume, so a reader that skipped them under-reported badly.
    /// </summary>
    [Fact]
    public async Task SubagentTranscriptsAreCounted()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddHours(-1)),
            ModelTurn("m1", Now.AddHours(-1)));

        _ = workspace.WriteLines(
            ChatsDirectory + "/" + SubagentParentId + "/" + SubagentSessionId + ".jsonl",
            Metadata(SubagentSessionId, Now.AddMinutes(-50), kind: "subagent"),
            ModelTurn("s1", Now.AddMinutes(-50)));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);
        TokenTotals tokens = Assert.IsType<TokenTotals>(usage.Tokens);

        Assert.Equal(1200L, tokens.Input);

        IReadOnlyList<AgentSession> sessions = await provider.GetSessionsAsync(Ct);
        Assert.Equal(2, sessions.Count);
    }

    /// <summary>
    /// The session id and start instant are on a file's first line and nowhere else, so they
    /// have to be remembered; a later pass that reads only appended message lines must not
    /// lose the session.
    /// </summary>
    [Fact]
    public async Task ASessionKeepsItsIdentityAcrossPasses()
    {
        using TempWorkspace workspace = CreateHome();
        DateTimeOffset startedAt = Now.AddHours(-3);
        string path = WriteMainSession(workspace, Metadata(MainSessionId, startedAt), ModelTurn("m1", Now.AddHours(-3)));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);
        _ = await provider.GetUsageAsync(Ct);

        workspace.Append(path, ModelTurn("m2", Now.AddMinutes(-20)) + "\n");
        await provider.RefreshAsync(Ct);

        AgentSession session = Assert.Single(await provider.GetSessionsAsync(Ct));

        Assert.Equal(MainSessionId, session.Id);
        Assert.Equal(startedAt, session.StartedAt);
        Assert.Equal(Now.AddMinutes(-20), session.LastActivityAt);
        Assert.Equal("gemini-3-pro", session.ModelId);
        Assert.Equal(1200L, session.Tokens?.Input);
    }

    /// <summary>
    /// Gemini CLI has no listing that resolves liveness, and a process cannot be attached to
    /// a session without reading its command line. No row claims to be the running one, which
    /// is the position the Codex reader already takes.
    /// </summary>
    [Fact]
    public async Task NoSessionRowEverClaimsToBeTheRunningOne()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(workspace, Metadata(MainSessionId, Now.AddMinutes(-2)), ModelTurn("m1", Now.AddMinutes(-1)));

        using GeminiUsageProvider provider = CreateProvider(
            workspace.Root,
            processes: new FakeProcessMonitor(new DetectedProcess(4321, "gemini", "gemini", Now.AddMinutes(-5))));

        ProviderUsage usage = await provider.GetUsageAsync(Ct);
        AgentSession session = Assert.Single(await provider.GetSessionsAsync(Ct));

        Assert.Equal(ProviderStatus.Active, usage.Status);
        Assert.False(session.IsActive);
    }

    [Fact]
    public async Task ARecentlyWrittenSessionIsActiveEvenWithNoProcessDetected()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(workspace, Metadata(MainSessionId, Now.AddMinutes(-9)), ModelTurn("m1", Now.AddMinutes(-3)));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        Assert.Equal(ProviderStatus.Active, usage.Status);
    }

    [Fact]
    public async Task AStoreWithNoRecentActivityIsIdleRatherThanActive()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(workspace, Metadata(MainSessionId, Now.AddHours(-5)), ModelTurn("m1", Now.AddHours(-4)));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        Assert.Equal(ProviderStatus.Idle, usage.Status);
    }

    /// <summary>
    /// Sessions from before the token counts existed. A single pretty-printed JSON document
    /// parses as no lines at all, which is the honest outcome: those files never carried a
    /// token count, so there is nothing in one to report.
    /// </summary>
    [Fact]
    public async Task ALegacySingleDocumentSessionFileContributesNoTokens()
    {
        using TempWorkspace workspace = CreateHome();
        _ = workspace.Write(
            ChatsDirectory + "/session-2026-01-15T09-00-0199cccc.json",
            """
            {
              "sessionId": "0199cccc-dddd-eeee-ffff-000011112222",
              "projectHash": "my-secret-project",
              "startTime": "2026-01-15T09:00:00Z",
              "messages": [
                { "id": "m1", "timestamp": "2026-01-15T09:01:00Z", "type": "gemini", "content": "no tokens here" }
              ]
            }
            """);

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        Assert.Null(usage.Tokens);
        Assert.Empty(usage.Metrics);
        Assert.Equal(ProviderStatus.Detected, usage.Status);
    }

    /// <summary>
    /// <b>The privacy boundary, asserted behaviourally.</b> Decoy session files are planted at
    /// every level above <c>chats</c> — the Gemini home itself, the <c>tmp</c> directory and
    /// the project directory — each holding a model turn with token counts of its own. If the
    /// enumeration ever starts above a <c>chats</c> directory, those tokens appear in the
    /// total and this goes red.
    /// </summary>
    /// <remarks>
    /// This is the test that has to be behavioural. Locking the credential files does not
    /// prove the enumeration is narrow, because the reader treats an unreadable file as one
    /// with nothing new in it — which is correct, and which means a read that reached a
    /// locked file would look exactly like one that never tried. A decoy the reader *can*
    /// open cannot be missed the same way. Verified: widening
    /// <c>GeminiPaths.FindChatDirectories</c> to return the project directory leaves the lock
    /// test green and turns this one red.
    /// </remarks>
    [Fact]
    public async Task AFileOutsideAChatsDirectoryIsNeverRead()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddHours(-1)),
            ModelTurn("m1", Now.AddHours(-1)));

        foreach (string decoy in new[] { "decoy-home.jsonl", "tmp/decoy-tmp.jsonl", "tmp/my-secret-project/decoy-project.jsonl" })
        {
            _ = workspace.WriteLines(
                decoy,
                Metadata("0199dddd-eeee-ffff-0000-111122223333", Now.AddHours(-1)),
                ModelTurn("decoy-" + decoy.Length.ToString(CultureInfo.InvariantCulture), Now.AddHours(-1), input: 7_000_000));
        }

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        // Exactly the one real session's turn, and none of the three decoys.
        Assert.Equal(600L, usage.Tokens?.Input);
        Assert.Single(await provider.GetSessionsAsync(Ct));
    }

    /// <summary>
    /// The same rule at the level it is enforced: the only directory handed to the file walk
    /// is a <c>chats</c> directory, so there is no path through the code that reaches the
    /// Gemini home rather than a filter that avoids one.
    /// </summary>
    [Fact]
    public void OnlyChatsDirectoriesAreEverEnumerated()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(workspace, Metadata(MainSessionId, Now));
        _ = workspace.WriteLines("tmp/another-project/chats/session-2026-09-15T13-00-0199eeee.jsonl", "{}");
        _ = workspace.Write("tmp/project-without-sessions/logs.json", "[]");

        IReadOnlyList<string> directories = GeminiPaths.FindChatDirectories(workspace.Root, maxProjects: 512);

        Assert.Equal(2, directories.Count);
        Assert.All(directories, static d => Assert.Equal("chats", Path.GetFileName(d)));
    }

    /// <summary>
    /// The credential files, held open with no sharing at all so that reading one throws.
    /// Narrower than the decoy test above and kept beside it: this one is specifically about
    /// the files that must never be opened whatever else changes.
    /// </summary>
    [Fact]
    public async Task ACredentialFileIsNeverOpened()
    {
        using TempWorkspace workspace = CreateHome();
        _ = WriteMainSession(
            workspace,
            Metadata(MainSessionId, Now.AddHours(-1)),
            ModelTurn("m1", Now.AddHours(-1)));

        string[] forbidden =
        [
            Path.Combine(workspace.Root, "oauth_creds.json"),
            Path.Combine(workspace.Root, "google_accounts.json"),
            Path.Combine(workspace.Root, "mcp-oauth-tokens-v2.json"),
            Path.Combine(workspace.Root, "settings.json"),
            Path.Combine(workspace.Root, "projects.json"),
            Path.Combine(workspace.Root, "tmp", "my-secret-project", ".project_root"),
            Path.Combine(workspace.Root, "tmp", "my-secret-project", "logs.json"),
        ];

        var locks = new List<FileStream>(forbidden.Length);
        try
        {
            foreach (string path in forbidden)
            {
                locks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));
            }

            using GeminiUsageProvider provider = CreateProvider(workspace.Root);
            ProviderUsage usage = await provider.GetUsageAsync(Ct);

            // A read that had touched any of those would have thrown an IOException, which
            // this provider turns into Error with no metrics and no tokens.
            Assert.NotEqual(ProviderStatus.Error, usage.Status);
            Assert.Equal(600L, usage.Tokens?.Input);
        }
        finally
        {
            foreach (FileStream handle in locks)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// The shape of one parsed line, against a fixture whose every string field is something
    /// that must not escape: the prompt, the reasoning, the model's answer, the project hash
    /// and the session's workspace directories.
    /// </summary>
    [Fact]
    public void AParsedLineCarriesNoContentProjectHashOrWorkspaceDirectory()
    {
        AssertNoContent(Metadata(MainSessionId, Now));
        AssertNoContent(ModelTurn("m1", Now));

        static void AssertNoContent(string json)
        {
            Assert.True(
                GeminiSessionScanner.TryParseLine(System.Text.Encoding.UTF8.GetBytes(json), out GeminiSessionLine line));

            foreach (System.Reflection.PropertyInfo property in typeof(GeminiSessionLine)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.GetValue(line) is not string value)
                {
                    continue;
                }

                Assert.DoesNotContain("must not carry", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("private-project", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("my-secret-project", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("\\", value, StringComparison.Ordinal);
                Assert.DoesNotContain("/", value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AUserTurnIsNotAModelTurnAndIsNeverCounted()
    {
        Assert.False(
            GeminiSessionScanner.TryParseLine(
                System.Text.Encoding.UTF8.GetBytes(UserTurn("u1", Now)),
                out GeminiSessionLine _));
    }

    /// <summary>
    /// A malformed line in the middle of a file is survivable. The recorder appends with a
    /// plain <c>appendFileSync</c> while the process may be killed at any moment, so a half
    /// written line is a thing that happens.
    /// </summary>
    [Fact]
    public async Task ACorruptLineIsSkippedRatherThanFailingTheRead()
    {
        using TempWorkspace workspace = CreateHome();
        _ = workspace.WriteLines(
            ChatsDirectory + "/session-2026-09-15T12-00-0199aaaa.jsonl",
            Metadata(MainSessionId, Now.AddHours(-1)),
            "{\"id\":\"broken\",\"type\":\"gemini\",\"tokens\":{",
            ModelTurn("m1", Now.AddHours(-1)));

        using GeminiUsageProvider provider = CreateProvider(workspace.Root);

        ProviderUsage usage = await provider.GetUsageAsync(Ct);

        Assert.NotEqual(ProviderStatus.Error, usage.Status);
        Assert.Equal(600L, usage.Tokens?.Input);
    }

    /// <summary>
    /// The environment variable the CLI itself reads, so that a relocated home is read rather
    /// than the user profile. It replaces the home directory, not the <c>.gemini</c> suffix.
    /// </summary>
    [Fact]
    public void TheHomeOverrideKeepsTheConfigDirectoryName()
    {
        string? previous = Environment.GetEnvironmentVariable(GeminiPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(GeminiPaths.HomeVariable, Path.Combine("X:", "relocated"));

            string? home = GeminiPaths.ResolveHome();

            Assert.Equal(Path.Combine("X:", "relocated", ".gemini"), home);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GeminiPaths.HomeVariable, previous);
        }
    }

    /// <summary>
    /// A Windows search pattern of <c>*.json</c> also matches <c>.jsonl</c> through short-name
    /// expansion, so the extension check has to be exact rather than a pattern.
    /// </summary>
    [Fact]
    public void OnlySessionFileExtensionsAreAccepted()
    {
        Assert.True(GeminiPaths.IsSessionFileName("session-2026-09-15T12-00-0199aaaa.jsonl"));
        Assert.True(GeminiPaths.IsSessionFileName("0199bbbb.json"));
        Assert.False(GeminiPaths.IsSessionFileName("logs.json.corrupted.1789515600.bak"));
        Assert.False(GeminiPaths.IsSessionFileName("shell_history"));
    }
}
