using System.Text.Json;
using Altim.Providers.Cli;

namespace Altim.Providers.Codex;

/// <summary>
/// Learns the CLI's authentication mode from <c>codex doctor --json</c>.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that the live quota call can be skipped when it is certain to fail. The
/// report is redacted by its author, which is why it is the source rather than
/// <c>auth.json</c>: Altim does not open credential files.
/// </para>
/// <para>
/// The exact property carrying the auth mode is not documented, so it is searched for by
/// name across a bounded depth rather than assumed at a fixed path, and anything
/// unrecognised produces <see cref="CodexAuthMode.Unknown"/>. Unknown means "go ahead and
/// try": only a definite non-ChatGPT answer suppresses the call, because treating a missing
/// field as a refusal would disable live quota for every CLI version that reports it
/// differently.
/// </para>
/// </remarks>
public sealed class CodexDoctorReader
{
    private const int MaxSearchDepth = 6;

    private static readonly string[] AuthModeNames =
    [
        "auth_mode", "authMode", "auth_method", "authMethod", "authentication_mode", "authenticationMode",
    ];

    private static readonly string[] AuthContainerNames = ["auth", "authentication", "account"];
    private static readonly string[] NestedModeNames = ["mode", "method", "type", "kind"];

    private readonly ICliRunner _runner;
    private readonly string _command;

    /// <summary>
    /// Creates a reader.
    /// </summary>
    /// <param name="runner">Runs the CLI.</param>
    /// <param name="command">The Codex CLI command name or path.</param>
    public CodexDoctorReader(ICliRunner runner, string command = "codex")
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        _runner = runner;
        _command = command;
    }

    /// <summary>
    /// Runs the doctor report and extracts the authentication mode.
    /// </summary>
    /// <param name="timeout">How long the report may take.</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>
    /// The mode, or <see cref="CodexAuthMode.Unknown"/> when the CLI is missing, the run
    /// failed, or the report did not carry a field this reader recognises.
    /// </returns>
    public async Task<CodexAuthMode> ReadAuthModeAsync(TimeSpan timeout, CancellationToken ct)
    {
        CliRunResult result = await _runner.RunAsync(_command, ["doctor", "--json"], timeout, ct).ConfigureAwait(false);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return CodexAuthMode.Unknown;
        }

        return ParseAuthMode(result.StandardOutput);
    }

    /// <summary>
    /// Extracts the authentication mode from a doctor report.
    /// </summary>
    /// <param name="json">The report as printed.</param>
    /// <returns>The mode, or <see cref="CodexAuthMode.Unknown"/>.</returns>
    public static CodexAuthMode ParseAuthMode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return CodexAuthMode.Unknown;
        }

        using (document)
        {
            string? raw = FindAuthMode(document.RootElement, MaxSearchDepth);
            return Interpret(raw);
        }
    }

    private static string? FindAuthMode(in JsonElement element, int depth)
    {
        if (depth <= 0 || element.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String && AuthModeNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String && AuthContainerNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }

            if (property.Value.ValueKind is JsonValueKind.Object && AuthContainerNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string nested in NestedModeNames)
                {
                    if (property.Value.TryGetProperty(nested, out JsonElement value) && value.ValueKind is JsonValueKind.String)
                    {
                        return value.GetString();
                    }
                }
            }
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string? found = FindAuthMode(property.Value, depth - 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static CodexAuthMode Interpret(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return CodexAuthMode.Unknown;
        }

        string normalized = raw.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (normalized.Contains("chatgpt", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("subscription", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("oauth", StringComparison.OrdinalIgnoreCase))
        {
            return CodexAuthMode.ChatGpt;
        }

        if (normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase))
        {
            return CodexAuthMode.ApiKey;
        }

        if (normalized.Contains("none", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("signedout", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("loggedout", StringComparison.OrdinalIgnoreCase))
        {
            return CodexAuthMode.NotAuthenticated;
        }

        return CodexAuthMode.Unknown;
    }
}
