using Altim.Core.Models;
using Altim.Providers.Codex;
using Altim.Providers.Codex.AppServer;
using Altim.Providers.Codex.State;
using Altim.Providers.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The state database is what makes the offline fallback affordable, and it belongs to
/// another program.
/// </summary>
/// <remarks>
/// <para>
/// Candidate rollout files come from <c>threads</c> ordered by last activity, so the reader
/// tails the newest handful instead of walking a session tree that measured 28.3 GB across
/// 2,518 files. Everything here follows from that: the file is owned by a CLI that is very
/// likely writing to it right now, its name carries a schema version that has changed
/// before, and a reader that cannot cope with either of those turns a working fallback into
/// a hard failure.
/// </para>
/// <para>
/// Every fixture is a database this test builds. No real <c>state_5.sqlite</c> is copied
/// into the repository.
/// </para>
/// </remarks>
public sealed class CodexStateDatabaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ColumnsAreDiscoveredSoASchemaThatSpellsThemDifferentlyStillReads()
    {
        // state_5 is not a contract. The filename carries a schema version, the number has
        // moved before, and a query naming a column a later schema dropped would fail the
        // whole fallback rather than one field of it.
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE threads (thread_id TEXT, model_slug TEXT, token_count INTEGER, path TEXT, last_activity_ms INTEGER, started_at_ms INTEGER)",
            "INSERT INTO threads VALUES ('0199aaaa', 'gpt-5.3-codex', 4242, 'C:\\sessions\\a.jsonl', 1789516800000, 1789513200000)");

        CodexThreadSummary thread = Assert.Single(new CodexStateDatabase(path).ReadRecentThreads(10));

        Assert.Equal("0199aaaa", thread.ThreadId);
        Assert.Equal("gpt-5.3-codex", thread.ModelId);
        Assert.Equal(4242L, thread.TokensUsed);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789516800000L), thread.UpdatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789513200000L), thread.CreatedAt);
    }

    [Fact]
    public void AColumnThisBuildHasNeverHeardOfIsIgnoredRatherThanRead()
    {
        // Only the columns the reader asked for are selected. A future schema that adds a
        // prompt preview or a working directory must not arrive in a summary by accident.
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE threads (id TEXT, cwd TEXT, first_user_message TEXT, updated_at_ms INTEGER, created_at_ms INTEGER)",
            "INSERT INTO threads VALUES ('0199aaaa', 'C:\\work\\private-project', 'do not read this', 1789516800000, 1789513200000)");

        CodexThreadSummary thread = Assert.Single(new CodexStateDatabase(path).ReadRecentThreads(10));

        Assert.Equal("0199aaaa", thread.ThreadId);
        Assert.Null(thread.ModelId);
        Assert.Null(thread.TokensUsed);
    }

    [Fact]
    public void ASchemaWithNothingTheReaderRecognisesYieldsNothingRatherThanThrowing()
    {
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE threads (title TEXT, flavour TEXT)",
            "INSERT INTO threads VALUES ('a', 'b')");

        Assert.Empty(new CodexStateDatabase(path).ReadRecentThreads(10));
    }

    [Fact]
    public void ADatabaseWithNoThreadsTableIsEmptyAndIsNotAFailure()
    {
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE conversations (id TEXT, updated_at_ms INTEGER)",
            "INSERT INTO conversations VALUES ('0199aaaa', 1789516800000)");

        Assert.Empty(new CodexStateDatabase(path).ReadRecentThreads(10));
    }

    [Fact]
    public void AMissingDatabaseIsEmptyAndIsNeverCreated()
    {
        // Opening read-write would bring an empty state database into existence inside
        // another program's home directory, which is not something a monitor gets to do.
        using var workspace = new TempWorkspace();
        string path = workspace.Path_("state_9.sqlite");

        Assert.Empty(new CodexStateDatabase(path).ReadRecentThreads(10));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AFileThatIsNotADatabaseAtAllIsEmptyRatherThanAnException()
    {
        using var workspace = new TempWorkspace();
        string path = workspace.Write("state_5.sqlite", "this is not a database");

        Assert.Empty(new CodexStateDatabase(path).ReadRecentThreads(10));
    }

    [Fact]
    public void ThreadsComeBackNewestFirstByLastActivity()
    {
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE threads (id TEXT, updated_at_ms INTEGER, created_at_ms INTEGER)",
            "INSERT INTO threads VALUES ('middle', 1789516800000, 1789513200000)",
            "INSERT INTO threads VALUES ('oldest', 1789430400000, 1789426800000)",
            "INSERT INTO threads VALUES ('newest', 1789603200000, 1789599600000)");

        IReadOnlyList<CodexThreadSummary> threads = new CodexStateDatabase(path).ReadRecentThreads(10);

        Assert.Equal("newest, middle, oldest", string.Join(", ", threads.Select(static t => t.ThreadId)));
    }

    [Fact]
    public void TheLimitIsHonouredSoTheNewestHandfulIsReadAndNotTheWholeTable()
    {
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE threads (id TEXT, updated_at_ms INTEGER, created_at_ms INTEGER)",
            "INSERT INTO threads VALUES ('a', 1789430400000, 1789426800000)",
            "INSERT INTO threads VALUES ('b', 1789516800000, 1789513200000)",
            "INSERT INTO threads VALUES ('c', 1789603200000, 1789599600000)");

        IReadOnlyList<CodexThreadSummary> threads = new CodexStateDatabase(path).ReadRecentThreads(2);

        Assert.Equal("c, b", string.Join(", ", threads.Select(static t => t.ThreadId)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodexStateDatabase(path).ReadRecentThreads(0));
    }

    [Fact]
    public void ARowWhoseMillisecondColumnIsNullFallsBackToTheOlderSecondColumn()
    {
        // The real schema carries both spellings. The millisecond columns were added later
        // and are null on older rows, so taking only the first spelling that exists dropped
        // the last-activity instant from every row written before the migration.
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE threads (id TEXT, updated_at_ms INTEGER, updated_at INTEGER, created_at_ms INTEGER, created_at INTEGER)",
            "INSERT INTO threads VALUES ('migrated', 1789516800000, 1789516800, 1789513200000, 1789513200)",
            "INSERT INTO threads VALUES ('older', NULL, 1789430400, NULL, 1789426800)");

        IReadOnlyList<CodexThreadSummary> threads = new CodexStateDatabase(path).ReadRecentThreads(10);

        Assert.Equal("migrated, older", string.Join(", ", threads.Select(static t => t.ThreadId)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789516800L), threads[0].UpdatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789430400L), threads[1].UpdatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789426800L), threads[1].CreatedAt);
    }

    [Fact]
    public void MillisecondsAndSecondsAreToldApartByMagnitudeRatherThanByColumnName()
    {
        // Naming is not evidence. The units have moved across schema versions, and reading
        // a millisecond value as seconds puts the row in the year 58,000 while reading a
        // second value as milliseconds puts it in 1970 — both of which reorder the list and
        // make a live session look ancient.
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE threads (id TEXT, updated_at INTEGER, created_at_ms INTEGER)",

            // A column named for seconds holding milliseconds, and one named for
            // milliseconds holding seconds.
            "INSERT INTO threads VALUES ('0199aaaa', 1789516800000, 1789513200)");

        CodexThreadSummary thread = Assert.Single(new CodexStateDatabase(path).ReadRecentThreads(10));

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789516800000L), thread.UpdatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789513200L), thread.CreatedAt);
    }

    [Fact]
    public void AnInstantThatIsZeroOrNegativeReadsAsUnavailableRatherThanAsNineteenSeventy()
    {
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE threads (id TEXT, tokens_used INTEGER, updated_at_ms INTEGER, created_at_ms INTEGER)",
            "INSERT INTO threads VALUES ('0199aaaa', -5, 0, -1)");

        CodexThreadSummary thread = Assert.Single(new CodexStateDatabase(path).ReadRecentThreads(10));

        Assert.Null(thread.UpdatedAt);
        Assert.Null(thread.CreatedAt);
        Assert.Null(thread.TokensUsed);
    }

    [Fact]
    public void AWalDatabaseIsReadWhileTheProgramThatOwnsItStillHasItOpen()
    {
        // The Codex CLI holds this file open and writes to it. The read is read-only, with
        // a busy timeout, so a commit in flight is ridden out rather than turning into
        // "database is locked" and a silent downgrade to picking files by modification
        // time. The owner's uncommitted work is not visible, and nothing here waits for it.
        using var workspace = new TempWorkspace();
        string path = CreateDatabase(
            workspace,
            "CREATE TABLE threads (id TEXT, updated_at_ms INTEGER, created_at_ms INTEGER)",
            "INSERT INTO threads VALUES ('committed-newer', 1789603200000, 1789599600000)",
            "INSERT INTO threads VALUES ('committed-older', 1789516800000, 1789513200000)");

        using var owner = new SqliteConnection(WritableConnectionString(path));
        owner.Open();
        Execute(owner, "PRAGMA journal_mode = WAL", null);

        using SqliteTransaction writing = owner.BeginTransaction();
        Execute(owner, "INSERT INTO threads VALUES ('uncommitted', 1789689600000, 1789686000000)", writing);

        IReadOnlyList<CodexThreadSummary> threads = new CodexStateDatabase(path).ReadRecentThreads(10);

        Assert.Equal("committed-newer, committed-older", string.Join(", ", threads.Select(static t => t.ThreadId)));

        writing.Rollback();
    }

    [Fact]
    public async Task TokensAreAttributedToTheThreadThatWroteThemWhenAThreadHasNoRolloutFile()
    {
        // The reviewer's bug, end to end. Token counts were stored by position in the
        // candidate list and read back by position in the thread list. Those line up only
        // while every thread has a rollout path: one row without one — an empty session, or
        // a schema that stopped recording the path — slid every later session's tokens onto
        // its neighbour, so one session was credited with another's usage and the last one
        // silently lost its own.
        using var workspace = new TempWorkspace();

        string first = workspace.WriteLines("sessions/2026/09/15/rollout-first.jsonl", RolloutLine(inputTokens: 1_000, outputTokens: 10));
        string third = workspace.WriteLines("sessions/2026/09/15/rollout-third.jsonl", RolloutLine(inputTokens: 3_000, outputTokens: 30));

        _ = CreateDatabase(
            workspace,
            "CREATE TABLE threads (id TEXT, model TEXT, rollout_path TEXT, updated_at_ms INTEGER, created_at_ms INTEGER)",
            "INSERT INTO threads VALUES ('thread-first', 'gpt-5.3-codex', " + Literal(first) + ", 1789603200000, 1789599600000)",

            // The row in the middle has no rollout file. It is still a thread, and it still
            // becomes a session; it just has no tokens of its own.
            "INSERT INTO threads VALUES ('thread-middle', 'gpt-5.3-codex', NULL, 1789516800000, 1789513200000)",
            "INSERT INTO threads VALUES ('thread-third', 'gpt-5.3-codex', " + Literal(third) + ", 1789430400000, 1789426800000)");

        using var provider = new CodexUsageProvider(
            CodexOptions.Default with { AllowNetworkCalls = false },
            new StubAppServerClient(CodexLiveResult.Skipped),
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now));

        IReadOnlyList<AgentSession> sessions = await provider.GetSessionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("thread-first, thread-middle, thread-third", string.Join(", ", sessions.Select(static s => s.Id)));
        Assert.Equal(1_000L, Session(sessions, "thread-first").Tokens?.Input);

        // Not 3,000: the middle row never wrote a rollout file, so it has nothing to report.
        Assert.Null(Session(sessions, "thread-middle").Tokens);

        // And the last row keeps its own figure instead of losing it off the end.
        Assert.Equal(3_000L, Session(sessions, "thread-third").Tokens?.Input);

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4_000L, usage.Tokens?.Input);
        Assert.Equal(40L, usage.Tokens?.Output);
    }

    [Fact]
    public async Task TwoThreadsSharingOneRolloutFileAreNotCountedTwice()
    {
        // Rollout paths are de-duplicated before the files are tailed, which is what keeps
        // an archived session that is still listed under its live path from doubling the
        // locally observed total.
        using var workspace = new TempWorkspace();
        string shared = workspace.WriteLines("sessions/2026/09/15/rollout-shared.jsonl", RolloutLine(inputTokens: 700, outputTokens: 7));

        _ = CreateDatabase(
            workspace,
            "CREATE TABLE threads (id TEXT, rollout_path TEXT, updated_at_ms INTEGER, created_at_ms INTEGER)",
            "INSERT INTO threads VALUES ('thread-a', " + Literal(shared) + ", 1789603200000, 1789599600000)",
            "INSERT INTO threads VALUES ('thread-b', " + Literal(shared) + ", 1789516800000, 1789513200000)");

        using var provider = new CodexUsageProvider(
            CodexOptions.Default with { AllowNetworkCalls = false },
            new StubAppServerClient(CodexLiveResult.Skipped),
            new FakeCliRunner { CommandExists = true },
            new FakeProcessMonitor(),
            workspace.Root,
            new FixedTimeProvider(Now));

        ProviderUsage usage = await provider.GetUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(700L, usage.Tokens?.Input);
    }

    private static AgentSession Session(IReadOnlyList<AgentSession> sessions, string id) =>
        Assert.Single(sessions, session => string.Equals(session.Id, id, StringComparison.Ordinal));

    private static string RolloutLine(long inputTokens, long outputTokens) =>
        "{\"type\":\"event_msg\",\"timestamp\":\"2026-09-15T12:00:00Z\",\"payload\":{\"type\":\"token_count\""
        + ",\"info\":{\"total_token_usage\":{\"input_tokens\":" + inputTokens
        + ",\"output_tokens\":" + outputTokens + "}}}}";

    /// <summary>A path as a SQL string literal, with the quote doubled.</summary>
    private static string Literal(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string WritableConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Pooling off so the fixture's handle is gone the moment it is closed and the
            // temporary directory can be deleted on Windows.
            Pooling = false,
        }.ToString();

    private static string CreateDatabase(TempWorkspace workspace, string schema, params string[] rows)
    {
        string path = workspace.Path_("state_5.sqlite");

        using (var connection = new SqliteConnection(WritableConnectionString(path)))
        {
            connection.Open();
            Execute(connection, schema, null);

            foreach (string row in rows)
            {
                Execute(connection, row, null);
            }
        }

        return path;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        _ = command.ExecuteNonQuery();
    }
}
