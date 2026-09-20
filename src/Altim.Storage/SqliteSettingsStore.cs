using System.Globalization;
using Altim.Core.Settings;
using Microsoft.Data.Sqlite;

namespace Altim.Storage;

/// <summary>
/// Typed access to the <c>setting</c> key/value table, over
/// <see cref="AltimSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every member has its own key, so adding a member never rewrites the others and an
/// older build reading a newer file simply ignores what it does not know.
/// </para>
/// <para>
/// Reading never throws over content. A key that is missing, empty, left over from an
/// older build, or edited by hand into nonsense yields that member's default, and the
/// rest of the record is unaffected. A settings table is not allowed to stop the
/// application starting.
/// </para>
/// </remarks>
public sealed class SqliteSettingsStore
{
    private readonly AltimDatabase _database;

    /// <summary>
    /// Creates the store over an open database.
    /// </summary>
    /// <param name="database">The open database.</param>
    public SqliteSettingsStore(AltimDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// The key each member of <see cref="AltimSettings"/> is stored under. Keys are
    /// stable across releases; a renamed key is a lost setting.
    /// </summary>
    public static class Keys
    {
        /// <summary>Key for <see cref="AltimSettings.Theme"/>.</summary>
        public const string Theme = "appearance.theme";

        /// <summary>Key for <see cref="AltimSettings.LaunchAtLogin"/>.</summary>
        public const string LaunchAtLogin = "startup.launch_at_login";

        /// <summary>Key for <see cref="AltimSettings.StartMinimised"/>.</summary>
        public const string StartMinimised = "startup.start_minimised";

        /// <summary>Key for <see cref="AltimSettings.NotificationsEnabled"/>.</summary>
        public const string NotificationsEnabled = "notifications.enabled";

        /// <summary>Key for <see cref="AltimSettings.NotifyOnThreshold"/>.</summary>
        public const string NotifyOnThreshold = "notifications.on_threshold";

        /// <summary>Key for <see cref="AltimSettings.NotifyOnWindowReset"/>.</summary>
        public const string NotifyOnWindowReset = "notifications.on_window_reset";

        /// <summary>Key for <see cref="AltimSettings.SessionThresholdPercent"/>.</summary>
        public const string SessionThreshold = "notifications.session_threshold";

        /// <summary>Key for <see cref="AltimSettings.WeeklyThresholdPercent"/>.</summary>
        public const string WeeklyThreshold = "notifications.weekly_threshold";

        /// <summary>Key for <see cref="AltimSettings.RefreshInterval"/>, in seconds.</summary>
        public const string RefreshSeconds = "monitoring.refresh_seconds";

        /// <summary>Key for <see cref="AltimSettings.ActiveRefreshInterval"/>, in seconds.</summary>
        public const string ActiveRefreshSeconds = "monitoring.active_refresh_seconds";

        /// <summary>Key for <see cref="AltimSettings.RefreshOnResume"/>.</summary>
        public const string RefreshOnResume = "monitoring.refresh_on_resume";

        /// <summary>Key for <see cref="AltimSettings.AllowNetworkCalls"/>.</summary>
        public const string AllowNetworkCalls = "providers.allow_network_calls";

        /// <summary>Key for <see cref="AltimSettings.ClaudeStatusLineEnabled"/>.</summary>
        public const string ClaudeStatusLine = "providers.claude_status_line";

        /// <summary>Key for <see cref="AltimSettings.AutomaticUpdateChecks"/>.</summary>
        public const string AutomaticUpdateChecks = "updates.automatic_checks";
    }

    /// <summary>
    /// Reads the whole settings record, defaulting every member that is missing or
    /// unreadable.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The stored settings, with defaults filled in.</returns>
    public async ValueTask<AltimSettings> GetAsync(CancellationToken ct)
    {
        Dictionary<string, string> values;

        using (SqliteConnection connection = _database.OpenRead())
        {
            values = await SettingTable.ReadAllAsync(connection, ct).ConfigureAwait(false);
        }

        AltimSettings defaults = AltimSettings.Default;

        return new AltimSettings
        {
            Theme = ReadTheme(values, Keys.Theme, defaults.Theme),
            LaunchAtLogin = ReadBool(values, Keys.LaunchAtLogin, defaults.LaunchAtLogin),
            StartMinimised = ReadBool(values, Keys.StartMinimised, defaults.StartMinimised),
            NotificationsEnabled = ReadBool(values, Keys.NotificationsEnabled,
                                            defaults.NotificationsEnabled),
            NotifyOnThreshold = ReadBool(values, Keys.NotifyOnThreshold, defaults.NotifyOnThreshold),
            NotifyOnWindowReset = ReadBool(values, Keys.NotifyOnWindowReset,
                                           defaults.NotifyOnWindowReset),
            SessionThresholdPercent = ReadPercent(values, Keys.SessionThreshold,
                                                  defaults.SessionThresholdPercent),
            WeeklyThresholdPercent = ReadPercent(values, Keys.WeeklyThreshold,
                                                 defaults.WeeklyThresholdPercent),
            RefreshInterval = ReadSeconds(values, Keys.RefreshSeconds, defaults.RefreshInterval),
            ActiveRefreshInterval = ReadSeconds(values, Keys.ActiveRefreshSeconds,
                                                defaults.ActiveRefreshInterval),
            RefreshOnResume = ReadBool(values, Keys.RefreshOnResume, defaults.RefreshOnResume),
            AllowNetworkCalls = ReadBool(values, Keys.AllowNetworkCalls, defaults.AllowNetworkCalls),
            ClaudeStatusLineEnabled = ReadBool(values, Keys.ClaudeStatusLine,
                                               defaults.ClaudeStatusLineEnabled),
            AutomaticUpdateChecks = ReadBool(values, Keys.AutomaticUpdateChecks,
                                             defaults.AutomaticUpdateChecks),
        };
    }

    /// <summary>
    /// Writes the whole settings record, in one transaction, so a crash mid-save cannot
    /// leave half a configuration behind.
    /// </summary>
    /// <param name="settings">What to store.</param>
    /// <param name="ct">Cancels the write.</param>
    public async ValueTask SaveAsync(AltimSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;

        using SqliteTransaction transaction = connection.BeginTransaction();

        await Write(connection, Keys.Theme, Format(settings.Theme), ct).ConfigureAwait(false);
        await Write(connection, Keys.LaunchAtLogin, Format(settings.LaunchAtLogin), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.StartMinimised, Format(settings.StartMinimised), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.NotificationsEnabled, Format(settings.NotificationsEnabled), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.NotifyOnThreshold, Format(settings.NotifyOnThreshold), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.NotifyOnWindowReset, Format(settings.NotifyOnWindowReset), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.SessionThreshold,
                    FormatPercent(settings.SessionThresholdPercent,
                                  AltimSettings.DefaultSessionThresholdPercent), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.WeeklyThreshold,
                    FormatPercent(settings.WeeklyThresholdPercent,
                                  AltimSettings.DefaultWeeklyThresholdPercent), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.RefreshSeconds, FormatSeconds(settings.RefreshInterval), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.ActiveRefreshSeconds,
                    FormatSeconds(settings.ActiveRefreshInterval), ct).ConfigureAwait(false);
        await Write(connection, Keys.RefreshOnResume, Format(settings.RefreshOnResume), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.AllowNetworkCalls, Format(settings.AllowNetworkCalls), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.ClaudeStatusLine, Format(settings.ClaudeStatusLineEnabled), ct)
            .ConfigureAwait(false);
        await Write(connection, Keys.AutomaticUpdateChecks,
                    Format(settings.AutomaticUpdateChecks), ct).ConfigureAwait(false);

        transaction.Commit();
    }

    /// <summary>
    /// Reads one raw value, for the handful of scalars that are not part of the settings
    /// record.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The stored string, or <see langword="null"/> when the key is absent.</returns>
    public async ValueTask<string?> GetValueAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        using SqliteConnection connection = _database.OpenRead();
        return await SettingTable.ReadAsync(connection, key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one raw value.
    /// </summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ct">Cancels the write.</param>
    public async ValueTask SetValueAsync(string key, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        await SettingTable.WriteAsync(lease.Connection, key, value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes one key, which puts the corresponding member back to its default.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <param name="ct">Cancels the write.</param>
    public async ValueTask RemoveValueAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        using WriteLease lease = await _database.LeaseWriterAsync(ct).ConfigureAwait(false);
        await SettingTable.RemoveAsync(lease.Connection, key, ct).ConfigureAwait(false);
    }

    private static ValueTask Write(SqliteConnection connection, string key, string value,
                                   CancellationToken ct)
        => SettingTable.WriteAsync(connection, key, value, ct);

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out string? raw) && bool.TryParse(raw, out bool parsed)
            ? parsed
            : fallback;

    private static int ReadPercent(Dictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out string? raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed is > 0 and <= 100
                ? parsed
                : fallback;

    private static TimeSpan ReadSeconds(Dictionary<string, string> values, string key,
                                        TimeSpan fallback)
        => values.TryGetValue(key, out string? raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : fallback;

    private static ThemePreference ReadTheme(Dictionary<string, string> values, string key,
                                             ThemePreference fallback)
    {
        if (!values.TryGetValue(key, out string? raw))
        {
            return fallback;
        }

        // An explicit map rather than Enum.TryParse: a stored "7" must not become a theme
        // that does not exist, and the stored spelling is part of the file format.
        return raw.Trim().ToLowerInvariant() switch
        {
            "system" => ThemePreference.System,
            "light" => ThemePreference.Light,
            "dark" => ThemePreference.Dark,
            _ => fallback,
        };
    }

    private static string Format(bool value) => value ? "true" : "false";

    private static string Format(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => "system",
    };

    private static string FormatPercent(int percent, int fallback)
        => (percent is > 0 and <= 100 ? percent : fallback).ToString(CultureInfo.InvariantCulture);

    private static string FormatSeconds(TimeSpan value)
        => ((int)Math.Max(1d, Math.Round(value.TotalSeconds, MidpointRounding.AwayFromZero)))
            .ToString(CultureInfo.InvariantCulture);
}
