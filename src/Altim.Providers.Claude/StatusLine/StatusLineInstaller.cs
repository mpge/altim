using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Altim.Providers.Claude.StatusLine;

/// <summary>
/// Merges Altim's status-line helper into the user's Claude Code settings, and takes it
/// back out again.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place in the provider layer that <b>writes to a file the user owns</b>,
/// and it is built to be boring about it:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Dry run by default.</b> Every entry point takes <c>dryRun</c> and it defaults to true,
/// so the accident of calling this class is a plan, not a change.
/// </item>
/// <item>
/// <b>Merge, never overwrite.</b> Existing settings are copied through property by property.
/// Nothing is reserialised from a model, so a key this build has never heard of survives
/// untouched.
/// </item>
/// <item>
/// <b>An existing status line is never replaced.</b> If the user already has one, the
/// install refuses and says so. Their status line is something they built; a monitoring app
/// silently taking it over would be indefensible.
/// </item>
/// <item>
/// <b>A backup is written first</b>, and revert removes only an entry Altim recognises as
/// its own by the marker in the command string.
/// </item>
/// </list>
/// <para>
/// The helper this installs must return in well under 100 milliseconds. Claude Code
/// debounces status-line updates at 300 milliseconds and cancels an in-flight script when a
/// newer update arrives, so the helper writes one small file and exits.
/// </para>
/// </remarks>
public sealed class StatusLineInstaller
{
    /// <summary>
    /// The marker that identifies a status-line command as Altim's own.
    /// </summary>
    /// <remarks>
    /// Revert matches on this rather than on the whole command string, so a user who edited
    /// the path or added a flag can still uninstall cleanly, and a command that is not
    /// Altim's is never removed.
    /// </remarks>
    public const string CommandMarker = "altim-statusline";

    /// <summary>The settings property that holds the status-line configuration.</summary>
    public const string SettingsProperty = "statusLine";

    private const string BackupSuffix = ".altim-backup";

    private readonly string _command;
    private readonly string? _configRootOverride;

    /// <summary>
    /// Creates an installer.
    /// </summary>
    /// <param name="command">
    /// The command line Claude Code should run. It must contain
    /// <see cref="CommandMarker"/>, so revert can recognise it later.
    /// </param>
    /// <param name="configRootOverride">
    /// The config root to write to. Defaults to the first root
    /// <see cref="ClaudePaths.ResolveConfigRoots"/> finds.
    /// </param>
    public StatusLineInstaller(string command, string? configRootOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (!command.Contains(CommandMarker, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The status-line command must contain the marker '" + CommandMarker + "' so revert can identify it.",
                nameof(command));
        }

        _command = command;
        _configRootOverride = configRootOverride;
    }

    /// <summary>
    /// Adds Altim's status-line entry, leaving every other setting alone.
    /// </summary>
    /// <param name="dryRun">
    /// True, the default, plans the change without writing. Pass false only after the user
    /// has agreed to it.
    /// </param>
    /// <returns>What happened, or what would happen.</returns>
    public StatusLineInstallResult Install(bool dryRun = true)
    {
        if (!TryResolveSettingsPath(out string? settingsPath))
        {
            return new StatusLineInstallResult(StatusLineInstallOutcome.NoConfigDirectory, dryRun, null, null);
        }

        if (!TryReadSettings(settingsPath, out JsonDocument? document, out bool missing))
        {
            return new StatusLineInstallResult(StatusLineInstallOutcome.SettingsUnreadable, dryRun, settingsPath, null);
        }

        using (document)
        {
            ExistingStatusLine existing = Inspect(document);
            if (existing is ExistingStatusLine.Altim)
            {
                return new StatusLineInstallResult(StatusLineInstallOutcome.AlreadyInstalled, dryRun, settingsPath, null);
            }

            if (existing is ExistingStatusLine.Foreign)
            {
                return new StatusLineInstallResult(StatusLineInstallOutcome.RefusedExistingStatusLine, dryRun, settingsPath, null);
            }

            if (dryRun)
            {
                return new StatusLineInstallResult(StatusLineInstallOutcome.WouldInstall, true, settingsPath, null);
            }

            string json = Rewrite(document, missing, includeStatusLine: true);
            return Commit(settingsPath, json, missing, StatusLineInstallOutcome.Installed);
        }
    }

    /// <summary>
    /// Removes Altim's status-line entry, and only Altim's.
    /// </summary>
    /// <param name="dryRun">
    /// True, the default, plans the change without writing.
    /// </param>
    /// <returns>What happened, or what would happen.</returns>
    public StatusLineInstallResult Revert(bool dryRun = true)
    {
        if (!TryResolveSettingsPath(out string? settingsPath))
        {
            return new StatusLineInstallResult(StatusLineInstallOutcome.NoConfigDirectory, dryRun, null, null);
        }

        if (!TryReadSettings(settingsPath, out JsonDocument? document, out bool missing) || missing)
        {
            return new StatusLineInstallResult(
                missing ? StatusLineInstallOutcome.NothingToRevert : StatusLineInstallOutcome.SettingsUnreadable,
                dryRun,
                settingsPath,
                null);
        }

        using (document)
        {
            if (Inspect(document) is not ExistingStatusLine.Altim)
            {
                // Either there is nothing there, or it belongs to the user. Leave it.
                return new StatusLineInstallResult(StatusLineInstallOutcome.NothingToRevert, dryRun, settingsPath, null);
            }

            if (dryRun)
            {
                return new StatusLineInstallResult(StatusLineInstallOutcome.WouldRevert, true, settingsPath, null);
            }

            string json = Rewrite(document, settingsMissing: false, includeStatusLine: false);
            return Commit(settingsPath, json, settingsMissing: false, StatusLineInstallOutcome.Reverted);
        }
    }

    private static bool TryReadSettings(string path, [NotNullWhen(true)] out JsonDocument? document, out bool missing)
    {
        document = null;
        missing = false;

        try
        {
            if (!File.Exists(path))
            {
                missing = true;
                document = JsonDocument.Parse("{}");
                return true;
            }

            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                missing = true;
                document = JsonDocument.Parse("{}");
                return true;
            }

            JsonDocument parsed = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
            if (parsed.RootElement.ValueKind is not JsonValueKind.Object)
            {
                parsed.Dispose();
                return false;
            }

            document = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? ReadCommand(in JsonElement statusLine)
    {
        if (statusLine.ValueKind is JsonValueKind.String)
        {
            return statusLine.GetString();
        }

        if (statusLine.ValueKind is JsonValueKind.Object
            && statusLine.TryGetProperty("command", out JsonElement command)
            && command.ValueKind is JsonValueKind.String)
        {
            return command.GetString();
        }

        return null;
    }

    private static StatusLineInstallResult Commit(string settingsPath, string json, bool settingsMissing, StatusLineInstallOutcome success)
    {
        string? backupPath = null;
        try
        {
            string? directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            if (!settingsMissing && File.Exists(settingsPath))
            {
                backupPath = settingsPath + BackupSuffix;
                File.Copy(settingsPath, backupPath, overwrite: true);
            }

            // Write beside the target and move into place, so an interrupted write cannot
            // leave the user with a truncated settings file.
            string temporaryPath = settingsPath + ".altim-tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, settingsPath, overwrite: true);

            return new StatusLineInstallResult(success, false, settingsPath, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new StatusLineInstallResult(StatusLineInstallOutcome.WriteFailed, false, settingsPath, backupPath);
        }
    }

    private bool TryResolveSettingsPath([NotNullWhen(true)] out string? settingsPath)
    {
        string? root = _configRootOverride;
        if (root is null)
        {
            IReadOnlyList<string> roots = ClaudePaths.ResolveConfigRoots();
            root = roots.Count > 0 ? roots[0] : null;
        }

        settingsPath = root is null ? null : ClaudePaths.SettingsFile(root);
        return settingsPath is not null;
    }

    private ExistingStatusLine Inspect(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty(SettingsProperty, out JsonElement statusLine)
            || statusLine.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ExistingStatusLine.None;
        }

        string? command = ReadCommand(statusLine);
        if (command is not null && command.Contains(CommandMarker, StringComparison.Ordinal))
        {
            return ExistingStatusLine.Altim;
        }

        return ExistingStatusLine.Foreign;
    }

    private string Rewrite(JsonDocument document, bool settingsMissing, bool includeStatusLine)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            if (!settingsMissing)
            {
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, SettingsProperty, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Copied through verbatim. Nothing is round-tripped through a model, so
                    // a setting this build does not know about is preserved exactly.
                    property.WriteTo(writer);
                }
            }

            if (includeStatusLine)
            {
                writer.WriteStartObject(SettingsProperty);
                writer.WriteString("type", "command");
                writer.WriteString("command", _command);
                writer.WriteNumber("padding", 0);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private enum ExistingStatusLine
    {
        None = 0,
        Altim = 1,
        Foreign = 2,
    }
}
