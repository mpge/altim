using System.Text.Json;
using Altim.Providers.Io;

namespace Altim.Providers.Claude.Transcripts;

/// <summary>
/// Scans Claude Code transcripts incrementally for token history.
/// </summary>
/// <remarks>
/// <para>
/// Four measured traps shape this class, and a reader that gets any of them wrong reports a
/// confidently wrong number:
/// </para>
/// <list type="number">
/// <item>
/// <b>Content blocks repeat the same usage object.</b> One transcript had 642 assistant
/// lines carrying 267 distinct <c>message.id</c> values; summing lines overcounted output
/// tokens by 3.15 times. Lines are de-duplicated on message identity, and the identity set
/// outlives a single pass so an incremental boundary between two lines sharing an id does
/// not reintroduce the double count.
/// </item>
/// <item>
/// <b>Subagent transcripts live in their own directory and dominate.</b> Including them took
/// one session from 24.8 million to 125.1 million cache-read tokens, and the store held
/// 2,056 subagent files against 22 main ones. They are included.
/// </item>
/// <item>
/// <b><c>&lt;synthetic&gt;</c> model entries are locally generated</b> and cost nothing, so
/// they are excluded. Model ids may also carry a <c>[1m]</c> long-context suffix with its
/// own pricing, so ids are kept exactly as written.
/// </item>
/// <item>
/// <b>Cache creation is split</b> into five-minute and one-hour fields and the one-hour tier
/// bills at twice the rate. Both are read separately; the flat
/// <c>cache_creation_input_tokens</c> field is used only when the split object is absent,
/// and is reported apart from the tiers.
/// </item>
/// </list>
/// <para>
/// A cold scan of 1.30 GB across 2,080 files took 6.7 seconds in plain Python. Steady state
/// here is the newly appended bytes of the few files that changed, because every file goes
/// through <see cref="IncrementalFileScanner"/>.
/// </para>
/// <para>
/// The format is disclaimed as internal by its vendor, so every field is optional and an
/// unexpected shape degrades a figure to unavailable rather than failing the scan.
/// </para>
/// </remarks>
public sealed class ClaudeTranscriptScanner
{
    /// <summary>
    /// The locally generated placeholder that costs nothing and is excluded from totals.
    /// It is the only value in the model field that means "no request was made".
    /// </summary>
    public const string SyntheticModelMarker = "<synthetic>";

    private readonly ClaudeOptions _options;
    private readonly IncrementalFileScanner _scanner;
    private readonly HashSet<string> _seenIdentities = new(StringComparer.Ordinal);
    private readonly Queue<string> _identityOrder = new();

    /// <summary>
    /// Creates a scanner.
    /// </summary>
    /// <param name="options">Budgets and windows. Defaults to <see cref="ClaudeOptions.Default"/>.</param>
    public ClaudeTranscriptScanner(ClaudeOptions? options = null)
    {
        _options = options ?? ClaudeOptions.Default;
        _scanner = new IncrementalFileScanner();
    }

    /// <summary>How many files the underlying incremental scanner is tracking.</summary>
    public int TrackedFileCount => _scanner.TrackedFileCount;

    /// <summary>
    /// Parses one transcript line.
    /// </summary>
    /// <param name="utf8Line">The line's bytes.</param>
    /// <param name="line">The parsed line when the method returns true.</param>
    /// <returns>
    /// True for an <c>assistant</c> line that carried a usage object. False for every other
    /// line, which is most of them: user turns, tool results and system entries all hold
    /// content and none of it is read.
    /// </returns>
    /// <exception cref="JsonException">The line is malformed.</exception>
    public static bool TryParseLine(ReadOnlyMemory<byte> utf8Line, out ClaudeUsageLine line)
    {
        line = default;

        using JsonDocument? document = JsonlTailReader.ParseObject(utf8Line);
        if (document is null)
        {
            return false;
        }

        JsonElement root = document.RootElement;
        string? type = JsonValues.ReadIdentifier(root, "type");
        if (!string.Equals(type, "assistant", StringComparison.Ordinal))
        {
            return false;
        }

        if (!JsonValues.TryGetObject(root, "message", null, out JsonElement message))
        {
            return false;
        }

        if (!JsonValues.TryGetObject(message, "usage", null, out JsonElement usage))
        {
            return false;
        }

        ReadModel(message, out string? modelId, out bool isSynthetic);

        long? cache5m = null;
        long? cache1h = null;
        long? cacheUnsplit = null;

        if (JsonValues.TryGetObject(usage, "cache_creation", "cacheCreation", out JsonElement cacheCreation))
        {
            cache5m = JsonValues.ReadCount(cacheCreation, "ephemeral_5m_input_tokens", "ephemeral5mInputTokens");
            cache1h = JsonValues.ReadCount(cacheCreation, "ephemeral_1h_input_tokens", "ephemeral1hInputTokens");
        }
        else
        {
            // Only when the tiered object is absent. Pricing this flat field as if it were
            // all five-minute cache under-reports, so it is carried separately and labelled.
            cacheUnsplit = JsonValues.ReadCount(usage, "cache_creation_input_tokens", "cacheCreationInputTokens");
        }

        line = new ClaudeUsageLine(
            JsonValues.ReadIdentifier(message, "id", "message_id"),
            JsonValues.ReadIdentifier(root, "requestId", "request_id"),
            JsonValues.ReadIdentifier(root, "sessionId", "session_id"),
            modelId,
            isSynthetic,
            JsonValues.ReadBoolean(root, "isSidechain", "is_sidechain") ?? false,
            JsonValues.ReadCount(usage, "input_tokens", "inputTokens"),
            JsonValues.ReadCount(usage, "output_tokens", "outputTokens"),
            JsonValues.ReadCount(usage, "cache_read_input_tokens", "cacheReadInputTokens"),
            cache5m,
            cache1h,
            cacheUnsplit,
            JsonValues.ReadIso8601(root, "timestamp"));

        return line.HasTokens || line.MessageId is not null;
    }

    /// <summary>
    /// Scans the transcripts under a set of config roots.
    /// </summary>
    /// <param name="configRoots">The config roots to scan.</param>
    /// <param name="now">The current instant, used to apply the transcript window.</param>
    /// <returns>
    /// What the new bytes contained. Repeating the call without an intervening write returns
    /// an empty history, because there is nothing new to read.
    /// </returns>
    public ClaudeTokenHistory Scan(IReadOnlyList<string> configRoots, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configRoots);

        var accumulator = new Accumulator();

        foreach (string root in configRoots)
        {
            foreach (string path in SelectTranscripts(ClaudePaths.ProjectsDirectory(root), now))
            {
                JsonlReadResult<ClaudeUsageLine> result = _scanner.ScanNew<ClaudeUsageLine>(path, TryParseLine);
                if (result.LinesConsidered == 0 && result.Values.Count == 0)
                {
                    continue;
                }

                accumulator.NoteFile(ClaudePaths.IsSubagentTranscript(path), result.LinesSkipped);

                foreach (ClaudeUsageLine line in result.Values)
                {
                    Accept(line, accumulator);
                }
            }
        }

        return accumulator.Build();
    }

    /// <summary>
    /// Scans one transcript file, for tests and for a targeted refresh.
    /// </summary>
    /// <param name="path">The transcript.</param>
    /// <returns>What the new bytes of that file contained.</returns>
    public ClaudeTokenHistory ScanFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var accumulator = new Accumulator();
        JsonlReadResult<ClaudeUsageLine> result = _scanner.ScanNew<ClaudeUsageLine>(path, TryParseLine);
        accumulator.NoteFile(ClaudePaths.IsSubagentTranscript(path), result.LinesSkipped);

        foreach (ClaudeUsageLine line in result.Values)
        {
            Accept(line, accumulator);
        }

        return accumulator.Build();
    }

    /// <summary>Forgets every file cursor and every remembered message identity.</summary>
    public void Reset()
    {
        _scanner.Reset();
        _seenIdentities.Clear();
        _identityOrder.Clear();
    }

    /// <summary>
    /// Decides what a line's model field means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the literal synthetic marker excludes a line. Everything else is a request that
    /// was made and tokens that were spent, whatever the id looks like.
    /// </para>
    /// <para>
    /// The previous rule — "anything that is not a plain identifier is synthetic" — deleted
    /// every line from a gateway deployment, because those ids carry characters an
    /// identifier may not: <c>us.anthropic.claude-…-v1:0</c> on Bedrock and
    /// <c>publishers/anthropic/models/…</c> on Vertex. That is not a placeholder, it is the
    /// user's whole usage. The id itself is still not carried out of the reader, because a
    /// string that failed the identifier test is exactly the kind of free text this reader
    /// refuses to hold; the tokens are counted against a null model id instead, which
    /// leaves them in every total and out of the per-model breakdown.
    /// </para>
    /// </remarks>
    private static void ReadModel(in JsonElement message, out string? modelId, out bool isSynthetic)
    {
        modelId = null;
        isSynthetic = false;

        if (!message.TryGetProperty("model", out JsonElement model) || model.ValueKind is not JsonValueKind.String)
        {
            return;
        }

        string? raw = model.GetString();
        if (string.Equals(raw, SyntheticModelMarker, StringComparison.OrdinalIgnoreCase))
        {
            isSynthetic = true;
            return;
        }

        modelId = JsonValues.IsIdentifier(raw) ? raw : null;
    }

    private void Accept(ClaudeUsageLine line, Accumulator accumulator)
    {
        if (line.IsSynthetic)
        {
            accumulator.NoteSynthetic();
            return;
        }

        if (!line.HasTokens)
        {
            return;
        }

        if (line.Identity is { } identity)
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

        accumulator.Add(line);
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

    /// <summary>
    /// Picks the transcripts this pass will read: the newest files inside the transcript
    /// window, at most <see cref="ClaudeOptions.MaxTranscriptFiles"/> of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retained set is kept newest-first <em>while</em> enumerating rather than by
    /// collecting everything and truncating at the end. A directory enumeration arrives in
    /// whatever order the filesystem hands it over, so a set that grew to the enumeration
    /// cap and was then cut would drop files by position rather than by age — and on a
    /// pathological store the files dropped could be the newest ones, which are the only
    /// ones that matter.
    /// </para>
    /// <para>
    /// The enumeration cap still bounds the walk itself. What it bounds now is how much of
    /// the store is inspected, not which of the inspected files survive.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string> SelectTranscripts(string projectsDirectory, DateTimeOffset now)
    {
        DateTime cutoff = (now - _options.TranscriptWindow).UtcDateTime;

        // A min-heap of the newest files seen so far: the oldest is always at the head, so
        // going over the budget evicts the oldest candidate and never the newest.
        var newest = new PriorityQueue<string, DateTime>();

        try
        {
            var directory = new DirectoryInfo(projectsDirectory);
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
                MaxRecursionDepth = 6,
            };

            int examined = 0;
            foreach (FileInfo file in directory.EnumerateFiles("*.jsonl", enumeration))
            {
                if (++examined > _options.MaxTranscriptFilesEnumerated)
                {
                    break;
                }

                DateTime written = file.LastWriteTimeUtc;
                if (written < cutoff)
                {
                    continue;
                }

                newest.Enqueue(file.FullName, written);
                if (newest.Count > _options.MaxTranscriptFiles)
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

    private sealed class Accumulator
    {
        private readonly Dictionary<string, ClaudeTokenBucket> _byModel = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ClaudeTokenBucket> _bySession = new(StringComparer.Ordinal);

        private ClaudeTokenBucket _totals;
        private int _duplicates;
        private int _synthetic;
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

        public void NoteSynthetic() => _synthetic++;

        public void NoteMissingIdentity() => _withoutIdentity++;

        public void Add(ClaudeUsageLine line)
        {
            _totals = _totals.Add(line);

            if (line.ModelId is { } modelId)
            {
                _byModel[modelId] = _byModel.TryGetValue(modelId, out ClaudeTokenBucket model)
                    ? model.Add(line)
                    : default(ClaudeTokenBucket).Add(line);
            }

            if (line.SessionId is { } sessionId)
            {
                _bySession[sessionId] = _bySession.TryGetValue(sessionId, out ClaudeTokenBucket session)
                    ? session.Add(line)
                    : default(ClaudeTokenBucket).Add(line);
            }

            if (line.Timestamp is { } timestamp)
            {
                if (_earliest is null || timestamp < _earliest)
                {
                    _earliest = timestamp;
                }

                if (_latest is null || timestamp > _latest)
                {
                    _latest = timestamp;
                }
            }
        }

        public ClaudeTokenHistory Build() => new(
            _totals,
            _byModel,
            _bySession,
            _duplicates,
            _synthetic,
            _withoutIdentity,
            _files,
            _subagentFiles,
            _corruptLines,
            _earliest,
            _latest);
    }
}
