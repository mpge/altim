using System.Text.Json;
using Altim.Providers.Cli;
using Altim.Providers.Io;

namespace Altim.Providers.Claude.Sessions;

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
    /// Lists the running agents.
    /// </summary>
    /// <param name="timeout">How long the command may take.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>
    /// The entries, or an empty list when the CLI is missing, failed, or reported none.
    /// Those cases are deliberately not distinguished here: the caller decides what "not
    /// detected" means using <see cref="IsAvailable"/>.
    /// </returns>
    public async Task<IReadOnlyList<ClaudeAgentEntry>> ListAsync(TimeSpan timeout, CancellationToken ct)
    {
        CliRunResult result = await _runner.RunAsync(_command, ["agents", "--json"], timeout, ct).ConfigureAwait(false);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        return Parse(result.StandardOutput);
    }

    /// <summary>
    /// Parses an agents listing.
    /// </summary>
    /// <param name="json">The listing as printed.</param>
    /// <returns>The entries, or an empty list when the output is not a JSON array.</returns>
    public static IReadOnlyList<ClaudeAgentEntry> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return [];
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
                return [];
            }

            var entries = new List<ClaudeAgentEntry>();
            foreach (JsonElement element in root.EnumerateArray())
            {
                if (element.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                long? pid = JsonValues.ReadInt64(element, "pid", "processId");
                DateTimeOffset? startedAt = JsonValues.ReadIso8601(element, "startedAt", "started_at")
                    ?? JsonValues.ReadIso8601(element, "startTime", "start_time")
                    ?? JsonValues.ReadUnixSeconds(element, "startedAt", "started_at");

                entries.Add(new ClaudeAgentEntry(
                    pid is >= 0 and <= int.MaxValue ? (int)pid.Value : null,
                    JsonValues.ReadIdentifier(element, "sessionId", "session_id"),
                    startedAt,
                    JsonValues.ReadIdentifier(element, "kind", "type"),
                    JsonValues.ReadIdentifier(element, "status", "state")));
            }

            return entries;
        }
    }
}
