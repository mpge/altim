using System.Text.Json;
using Altim.Providers.Io;

namespace Altim.Providers.Gemini.Sessions;

/// <summary>
/// Scans Gemini CLI session transcripts incrementally for token history.
/// </summary>
/// <remarks>
/// <para>
/// Gemini CLI writes one append-only JSON Lines file per session under
/// <c>&lt;gemini home&gt;/tmp/&lt;project&gt;/chats/</c>, and every model turn that reported
/// usage carries the response's own <c>usageMetadata</c> counts. That is the only local,
/// zero-cost source of Gemini usage on this machine: nothing about remaining quota is ever
/// written to disk. See <c>PROVIDERS.md</c>.
/// </para>
/// <para>Four things shape this class, and a reader that gets any of them wrong is confidently wrong:</para>
/// <list type="number">
/// <item>
/// <b>The same message id is appended repeatedly.</b> The recorder writes a model turn when
/// it completes, writes the whole record again when the response's token counts arrive, and
/// again when its tool calls are enriched. Gemini CLI's own loader keys messages by id and
/// keeps the last, so summing lines counts the same tokens several times over. Lines are
/// de-duplicated on message id, and the identity set outlives a single pass so an
/// incremental boundary between two lines sharing an id does not reintroduce the double
/// count.
/// </item>
/// <item>
/// <b>The session id is on the first line only.</b> Message records do not repeat it, so a
/// pass that resumes part way through a growing file never sees it. The identity of each
/// file is therefore read once and remembered.
/// </item>
/// <item>
/// <b>Subagent transcripts live one directory deeper</b>, named after the parent session
/// rather than with the <c>session-</c> prefix. They are included: on the other
/// transcript-based provider, subagent files carried most of the token volume.
/// </item>
/// <item>
/// <b>A resumed session is rewritten, not appended to.</b> The recorder rewrites the whole
/// file when it resumes one, so a cursor into it can be stale. The incremental scanner
/// re-reads a file that shrank from the beginning, and de-duplication on message id is what
/// makes that safe: nothing already counted is counted again.
/// </item>
/// </list>
/// <para>
/// <b>Nothing outside a <c>chats</c> directory is listed or opened.</b> Every enumeration
/// this class performs is rooted at one, which is three levels below the Gemini home where
/// the credential files live. Metadata lines carry the session's workspace directories as
/// absolute paths and the file's project hash; message lines carry prompts, reasoning, tool
/// arguments and tool output. None of it survives <see cref="TryParseLine"/>, which returns
/// counts, instants and two opaque identifiers.
/// </para>
/// <para>
/// The format is undocumented and the vendor has already changed it: the same files were one
/// JSON document per session, with no token counts anywhere in them, seventy-odd releases
/// ago. Every field is optional and an unexpected shape degrades a figure to unavailable
/// rather than failing the scan.
/// </para>
/// </remarks>
public sealed class GeminiSessionScanner
{
    private readonly GeminiOptions _options;
    private readonly IncrementalFileScanner _scanner;
    private readonly HashSet<string> _seenIdentities = new(StringComparer.Ordinal);
    private readonly Queue<string> _identityOrder = new();

    /// <summary>
    /// What each session file's first line said, keyed by path. Paths are dictionary keys
    /// here and nothing else: they are never returned, persisted or logged, exactly as in
    /// <see cref="IncrementalFileScanner"/>.
    /// </summary>
    private readonly Dictionary<string, FileIdentity> _fileIdentities;
    private readonly Queue<string> _fileOrder = new();

    /// <summary>
    /// Creates a scanner.
    /// </summary>
    /// <param name="options">Budgets and windows. Defaults to <see cref="GeminiOptions.Default"/>.</param>
    public GeminiSessionScanner(GeminiOptions? options = null)
    {
        _options = options ?? GeminiOptions.Default;
        _scanner = new IncrementalFileScanner();
        _fileIdentities = new Dictionary<string, FileIdentity>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    /// <summary>How many files the underlying incremental scanner is tracking.</summary>
    public int TrackedFileCount => _scanner.TrackedFileCount;

    /// <summary>
    /// Parses one session-file line.
    /// </summary>
    /// <param name="utf8Line">The line's bytes.</param>
    /// <param name="line">The parsed line when the method returns true.</param>
    /// <returns>
    /// True for a metadata line and for a model turn. False for every other line, which is
    /// most of them: user turns, tool results, rewind markers and metadata patches all hold
    /// content or nothing this reader wants, and none of it is read.
    /// </returns>
    /// <exception cref="JsonException">The line is malformed.</exception>
    public static bool TryParseLine(ReadOnlyMemory<byte> utf8Line, out GeminiSessionLine line)
    {
        line = default;

        using JsonDocument? document = JsonlTailReader.ParseObject(utf8Line);
        if (document is null)
        {
            return false;
        }

        JsonElement root = document.RootElement;

        // A metadata line names the session and carries no message id. It is the only line
        // in the file that says which session the file belongs to.
        if (JsonValues.ReadIdentifier(root, "sessionId", "session_id") is { } sessionId)
        {
            line = new GeminiSessionLine(
                GeminiLineKind.Metadata,
                sessionId,
                MessageId: null,
                ModelId: null,
                JsonValues.ReadIso8601(root, "startTime", "start_time"),
                JsonValues.ReadIso8601(root, "lastUpdated", "last_updated"),
                null,
                null,
                null,
                null,
                null,
                null);
            return true;
        }

        // Only a model turn can carry usage. A user turn, an info line and an error line are
        // all message records too, and none of them ever has a token count.
        if (!string.Equals(JsonValues.ReadIdentifier(root, "type"), "gemini", StringComparison.Ordinal))
        {
            return false;
        }

        string? messageId = JsonValues.ReadIdentifier(root, "id");
        if (messageId is null)
        {
            return false;
        }

        long? prompt = null;
        long? output = null;
        long? cached = null;
        long? thoughts = null;
        long? tool = null;
        long? total = null;

        if (JsonValues.TryGetObject(root, "tokens", null, out JsonElement tokens))
        {
            prompt = JsonValues.ReadCount(tokens, "input");
            output = JsonValues.ReadCount(tokens, "output");
            cached = JsonValues.ReadCount(tokens, "cached");
            thoughts = JsonValues.ReadCount(tokens, "thoughts");
            tool = JsonValues.ReadCount(tokens, "tool");
            total = JsonValues.ReadCount(tokens, "total");
        }

        line = new GeminiSessionLine(
            GeminiLineKind.Message,
            SessionId: null,
            messageId,
            JsonValues.ReadIdentifier(root, "model"),
            StartedAt: null,
            JsonValues.ReadIso8601(root, "timestamp"),
            prompt,
            output,
            cached,
            thoughts,
            tool,
            total);

        return true;
    }

    /// <summary>
    /// Scans the session files under a Gemini home.
    /// </summary>
    /// <param name="home">The Gemini home, or <see langword="null"/> when there is none.</param>
    /// <param name="now">The current instant, used to apply the session window.</param>
    /// <returns>
    /// What the new bytes contained. Repeating the call without an intervening write returns
    /// an empty history, because there is nothing new to read.
    /// </returns>
    public GeminiTokenHistory Scan(string? home, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(home))
        {
            return GeminiTokenHistory.Empty;
        }

        var accumulator = new Accumulator();

        foreach (string chats in GeminiPaths.FindChatDirectories(home, _options.MaxProjectDirectories))
        {
            foreach (string path in SelectSessionFiles(chats, now))
            {
                ScanFileInto(path, GeminiPaths.IsSubagentSession(chats, path), accumulator);
            }
        }

        return accumulator.Build();
    }

    /// <summary>
    /// Scans one session file, for tests and for a targeted refresh.
    /// </summary>
    /// <param name="path">The session file.</param>
    /// <param name="isSubagent">Whether the file is a subagent's transcript.</param>
    /// <returns>What the new bytes of that file contained.</returns>
    public GeminiTokenHistory ScanFile(string path, bool isSubagent = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var accumulator = new Accumulator();
        ScanFileInto(path, isSubagent, accumulator);
        return accumulator.Build();
    }

    /// <summary>Forgets every file cursor, identity and remembered message.</summary>
    public void Reset()
    {
        _scanner.Reset();
        _seenIdentities.Clear();
        _identityOrder.Clear();
        _fileIdentities.Clear();
        _fileOrder.Clear();
    }

    private void ScanFileInto(string path, bool isSubagent, Accumulator accumulator)
    {
        JsonlReadResult<GeminiSessionLine> result = _scanner.ScanNew<GeminiSessionLine>(path, TryParseLine);
        if (result.LinesConsidered == 0 && result.Values.Count == 0)
        {
            return;
        }

        accumulator.NoteFile(isSubagent, result.LinesSkipped);

        string? sessionId = null;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? lastActivity = null;
        string? modelId = null;

        if (_fileIdentities.TryGetValue(path, out FileIdentity remembered))
        {
            sessionId = remembered.SessionId;
            startedAt = remembered.StartedAt;
        }

        foreach (GeminiSessionLine line in result.Values)
        {
            if (line.Kind is GeminiLineKind.Metadata)
            {
                sessionId = line.SessionId ?? sessionId;
                startedAt = line.StartedAt ?? startedAt;
                lastActivity = Newest(lastActivity, line.Timestamp);
                continue;
            }

            lastActivity = Newest(lastActivity, line.Timestamp);
            modelId = line.ModelId ?? modelId;
            Accept(line, sessionId, accumulator);
        }

        if (sessionId is not null)
        {
            RememberFile(path, new FileIdentity(sessionId, startedAt));
            accumulator.NoteSession(new GeminiSessionRecord(sessionId, startedAt, lastActivity, modelId, isSubagent));
        }
    }

    private static DateTimeOffset? Newest(DateTimeOffset? current, DateTimeOffset? candidate)
    {
        if (candidate is not { } value)
        {
            return current;
        }

        return current is { } incumbent && incumbent >= value ? incumbent : value;
    }

    private void Accept(GeminiSessionLine line, string? sessionId, Accumulator accumulator)
    {
        if (!line.HasTokens)
        {
            // A model turn whose usage figures had not arrived when it was written. The
            // recorder appends the same message again once they do, and that copy is the one
            // that counts.
            return;
        }

        if (line.MessageId is { } identity)
        {
            if (!Remember(identity))
            {
                accumulator.NoteDuplicate();
                return;
            }
        }
        else
        {
            accumulator.NoteMissingIdentity();
        }

        accumulator.Add(line, sessionId);
    }

    private bool Remember(string identity)
    {
        if (!_seenIdentities.Add(identity))
        {
            return false;
        }

        _identityOrder.Enqueue(identity);
        while (_identityOrder.Count > _options.MessageIdentityMemory)
        {
            _ = _seenIdentities.Remove(_identityOrder.Dequeue());
        }

        return true;
    }

    private void RememberFile(string path, FileIdentity identity)
    {
        bool isNew = !_fileIdentities.ContainsKey(path);
        _fileIdentities[path] = identity;

        if (!isNew)
        {
            return;
        }

        _fileOrder.Enqueue(path);
        while (_fileOrder.Count > _options.SessionIdentityMemory)
        {
            _ = _fileIdentities.Remove(_fileOrder.Dequeue());
        }
    }

    /// <summary>
    /// Picks the session files this pass will read: the newest inside the session window, at
    /// most <see cref="GeminiOptions.MaxSessionFiles"/> of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The enumeration is rooted at a <c>chats</c> directory and descends two levels, which
    /// is the main sessions and the per-parent subagent directories beneath them. That root
    /// is the privacy boundary: nothing above it, and in particular nothing in the Gemini
    /// home itself, is ever listed.
    /// </para>
    /// <para>
    /// The retained set is kept newest-first <em>while</em> enumerating rather than by
    /// collecting everything and truncating at the end, because a directory enumeration
    /// arrives in whatever order the filesystem hands it over and a set cut at the end would
    /// drop files by position rather than by age.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string> SelectSessionFiles(string chatsDirectory, DateTimeOffset now)
    {
        DateTime cutoff = (now - _options.SessionWindow).UtcDateTime;
        var newest = new PriorityQueue<string, DateTime>();

        try
        {
            var directory = new DirectoryInfo(chatsDirectory);
            if (!directory.Exists)
            {
                return [];
            }

            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = 2,
            };

            int examined = 0;
            foreach (FileInfo file in directory.EnumerateFiles("*.json*", enumeration))
            {
                if (++examined > _options.MaxSessionFilesEnumerated)
                {
                    break;
                }

                if (!GeminiPaths.IsSessionFileName(file.Name))
                {
                    continue;
                }

                DateTime written = file.LastWriteTimeUtc;
                if (written < cutoff)
                {
                    continue;
                }

                newest.Enqueue(file.FullName, written);
                if (newest.Count > _options.MaxSessionFiles)
                {
                    _ = newest.Dequeue();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }

        var selected = new List<(string Path, DateTime Written)>(newest.Count);
        while (newest.TryDequeue(out string? path, out DateTime written))
        {
            selected.Add((path, written));
        }

        selected.Sort(static (a, b) => b.Written.CompareTo(a.Written));
        return selected.Select(static c => c.Path).ToList();
    }

    private readonly record struct FileIdentity(string SessionId, DateTimeOffset? StartedAt);

    private sealed class Accumulator
    {
        private readonly Dictionary<string, GeminiTokenBucket> _byModel = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GeminiTokenBucket> _bySession = new(StringComparer.Ordinal);
        private readonly Dictionary<DateOnly, GeminiTokenBucket> _byDay = [];
        private readonly List<GeminiSessionRecord> _sessions = [];

        private GeminiTokenBucket _totals;
        private int _duplicates;
        private int _withoutIdentity;
        private int _files;
        private int _subagentFiles;
        private int _corruptLines;
        private DateTimeOffset? _earliest;
        private DateTimeOffset? _latest;

        public void NoteFile(bool isSubagent, int corruptLines)
        {
            _files++;
            if (isSubagent)
            {
                _subagentFiles++;
            }

            _corruptLines += corruptLines;
        }

        public void NoteDuplicate() => _duplicates++;

        public void NoteMissingIdentity() => _withoutIdentity++;

        public void NoteSession(GeminiSessionRecord session) => _sessions.Add(session);

        public void Add(GeminiSessionLine line, string? sessionId)
        {
            _totals = _totals.Add(line);

            if (line.ModelId is { } modelId)
            {
                _byModel[modelId] = Existing(_byModel, modelId).Add(line);
            }

            if (sessionId is not null)
            {
                _bySession[sessionId] = Existing(_bySession, sessionId).Add(line);
            }

            if (line.Timestamp is not { } timestamp)
            {
                // It happened; the file does not say when. Putting it on today would be
                // inventing that, so it counts towards the totals and towards no day.
                return;
            }

            DateOnly day = DateOnly.FromDateTime(timestamp.ToLocalTime().DateTime);
            _byDay[day] = _byDay.TryGetValue(day, out GeminiTokenBucket bucket)
                ? bucket.Add(line)
                : default(GeminiTokenBucket).Add(line);

            _earliest = _earliest is { } earliest && earliest <= timestamp ? earliest : timestamp;
            _latest = _latest is { } latest && latest >= timestamp ? latest : timestamp;
        }

        public GeminiTokenHistory Build() => new(
            _totals,
            _byModel,
            _bySession,
            _byDay,
            _sessions,
            _duplicates,
            _withoutIdentity,
            _files,
            _subagentFiles,
            _corruptLines,
            _earliest,
            _latest);

        private static GeminiTokenBucket Existing(Dictionary<string, GeminiTokenBucket> buckets, string key) =>
            buckets.TryGetValue(key, out GeminiTokenBucket bucket) ? bucket : default;
    }
}
