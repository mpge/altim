using System.Text.Json;
using Altim.Providers.Cli;
using Altim.Providers.Io;

namespace Altim.Providers.Claude.Sessions;

/// <summary>
/// How an agents listing ended.
/// </summary>
public enum ClaudeAgentsOutcome
{
    /// <summary>The command answered. The entries are what it reported, and may be none.</summary>
    Listed = 0,

    /// <summary>The Claude Code CLI is not on this machine.</summary>
    NotDetected = 1,

    /// <summary>
    /// The command ran and did not produce a listing: a non-zero exit, a timeout, or output
    /// that is not a JSON array.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The command was not run, because the last listing is recent enough to still stand.
    /// </summary>
    /// <remarks>
    /// Not the same as a failure. A failed listing is evidence that the command cannot
    /// answer; a skipped one is no new evidence at all, so the caller keeps the answer it
    /// already had rather than falling back to anything.
    /// </remarks>
    Skipped = 3,
}

/// <summary>
/// The result of one agents listing.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Entries">
/// What the command reported. Empty for every outcome other than
/// <see cref="ClaudeAgentsOutcome.Listed"/>.
/// </param>
/// <remarks>
/// "No sessions are running" and "the listing could not be had" are different facts and the
/// caller acts differently on them. An empty <b>listing</b> is authoritative — the command
/// resolves liveness itself, and a stale session registry on the verification machine
/// claimed an idle session whose process id had been recycled while the command correctly
/// omitted it — so process enumeration must not second-guess it. A <b>failed</b> listing is
/// no information at all, and process enumeration is exactly the fallback for it.
/// </remarks>
public sealed record ClaudeAgentsListing(ClaudeAgentsOutcome Outcome, IReadOnlyList<ClaudeAgentEntry> Entries)
{
    /// <summary>A listing that could not be had.</summary>
    public static ClaudeAgentsListing Failed { get; } = new(ClaudeAgentsOutcome.Failed, []);

    /// <summary>A listing from a machine without the CLI.</summary>
    public static ClaudeAgentsListing NotDetected { get; } = new(ClaudeAgentsOutcome.NotDetected, []);

    /// <summary>A listing that was not asked for, because the last one still stands.</summary>
    public static ClaudeAgentsListing Skipped { get; } = new(ClaudeAgentsOutcome.Skipped, []);

    /// <summary>True when the command answered, whatever it said.</summary>
    public bool IsAuthoritative => Outcome is ClaudeAgentsOutcome.Listed;
}

/// <summary>
/// Reads running sessions from <c>claude agents --json</c>.
/// </summary>
/// <remarks>
/// The CLI resolves liveness itself, so this is preferred over process enumeration. The
/// process scanner remains the fallback for when the CLI is missing or refuses, and it
/// records an executable name and a boolean and nothing else.
/// </remarks>
public sealed class ClaudeAgentsReader
{
    private const string DefaultCommand = "claude";

    private readonly ICliRunner _runner;
    private readonly string _command;

    /// <summary>
    /// Creates a reader.
    /// </summary>
    /// <param name="runner">Runs the CLI.</param>
    /// <param name="command">The Claude Code command name or path.</param>
    public ClaudeAgentsReader(ICliRunner runner, string command = DefaultCommand)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        _runner = runner;
        _command = command;
    }

    /// <summary>True when the Claude Code CLI exists on this machine.</summary>
    public bool IsAvailable => _runner.Exists(_command);

    /// <summary>
    /// Parses an agents listing.
    /// </summary>
    /// <param name="json">The listing as printed.</param>
    /// <returns>The entries, or an empty list when the output is not a JSON array.</returns>
    public static IReadOnlyList<ClaudeAgentEntry> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return TryParse(json, out IReadOnlyList<ClaudeAgentEntry> entries) ? entries : [];
    }

    /// <summary>
    /// Parses an agents listing, distinguishing "the command said none" from "this is not a
    /// listing".
    /// </summary>
    /// <param name="json">The listing as printed.</param>
    /// <param name="entries">The entries when the method returns true.</param>
    /// <returns>True when the output was a listing.</returns>
    public static bool TryParse(string json, out IReadOnlyList<ClaudeAgentEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(json);

        entries = [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind is JsonValueKind.Object && root.TryGetProperty("agents", out JsonElement nested))
            {
                root = nested;
            }

            if (root.ValueKind is not JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<ClaudeAgentEntry>();
            foreach (JsonElement element in root.EnumerateArray())
            {
                if (element.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                long? pid = JsonValues.ReadInt64(element, "pid", "processId");

                // The installed CLI reports startedAt as Unix milliseconds. A reader that
                // accepted only ISO 8601 and Unix seconds discarded every one of them as
                // implausible, and the caller then had no start time to report.
                DateTimeOffset? startedAt = JsonValues.ReadIso8601(element, "startedAt", "started_at")
                    ?? JsonValues.ReadIso8601(element, "startTime", "start_time")
                    ?? JsonValues.ReadUnixTimestamp(element, "startedAt", "started_at")
                    ?? JsonValues.ReadUnixTimestamp(element, "startTime", "start_time");

                parsed.Add(new ClaudeAgentEntry(
                    pid is >= 0 and <= int.MaxValue ? (int)pid.Value : null,
                    JsonValues.ReadIdentifier(element, "sessionId", "session_id"),
                    startedAt,
                    JsonValues.ReadIdentifier(element, "kind", "type"),
                    JsonValues.ReadIdentifier(element, "status", "state")));
            }

            entries = parsed;
            return true;
        }
    }

    /// <summary>
    /// Lists the running agents.
    /// </summary>
    /// <param name="timeout">How long the command may take.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>
    /// The listing, with an outcome that says whether the command answered. An empty
    /// listing is an answer; a failed one is not, and only a failed one sends the caller to
    /// process enumeration.
    /// </returns>
    public async Task<ClaudeAgentsListing> ListAsync(TimeSpan timeout, CancellationToken ct)
    {
        CliRunResult result = await _runner.RunAsync(_command, ["agents", "--json"], timeout, ct).ConfigureAwait(false);

        if (result.Outcome is CliRunOutcome.NotDetected)
        {
            return ClaudeAgentsListing.NotDetected;
        }

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return ClaudeAgentsListing.Failed;
        }

        return TryParse(result.StandardOutput, out IReadOnlyList<ClaudeAgentEntry> entries)
            ? new ClaudeAgentsListing(ClaudeAgentsOutcome.Listed, entries)
            : ClaudeAgentsListing.Failed;
    }
}
