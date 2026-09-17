using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Core.Settings;
using Altim.UI.Formatting;
using Altim.UI.Services;
using AvaloniaDesign = Avalonia.Controls.Design;

namespace Altim.UI.ViewModels.Design;

/// <summary>
/// The view models the XAML previewer shows.
/// </summary>
/// <remarks>
/// <para>
/// Every member returns <see langword="null"/> unless <see cref="AvaloniaDesign.IsDesignMode"/> is set,
/// so the stand-in provider, history and settings types below are never constructed by the
/// running application. A view binds one of these through <c>Design.DataContext</c>, which
/// Avalonia only applies in the previewer.
/// </para>
/// <para>
/// The data is deliberately awkward rather than tidy: one provider reports two windows and
/// tokens, the other reports a window it has no percentage for, and a third is unreachable. A
/// previewer that only ever shows healthy numbers hides the states that matter.
/// </para>
/// </remarks>
public static class DesignData
{
    private static DashboardViewModel? _dashboard;
    private static PopupViewModel? _popup;

    /// <summary>The tray panel, populated.</summary>
    public static PopupViewModel? Popup => AvaloniaDesign.IsDesignMode ? BuildPopup() : null;

    /// <summary>The dashboard window, populated.</summary>
    public static DashboardViewModel? Dashboard => AvaloniaDesign.IsDesignMode ? BuildDashboard() : null;

    /// <summary>The Overview page, populated.</summary>
    public static OverviewViewModel? Overview => Dashboard?.Overview;

    /// <summary>A provider page, populated.</summary>
    public static ProviderPageViewModel? ProviderPage =>
        Dashboard is { } dashboard ? new ProviderPageViewModel(dashboard.Providers[0]) : null;

    /// <summary>The History page, populated.</summary>
    public static HistoryViewModel? History
    {
        get
        {
            if (Dashboard is not { } dashboard)
            {
                return null;
            }

            _ = dashboard.History.LoadAsync(CancellationToken.None);
            return dashboard.History;
        }
    }

    /// <summary>One provider card, populated.</summary>
    public static ProviderViewModel? Provider => Dashboard?.Providers[0];

    /// <summary>One metric row, reported and above its threshold.</summary>
    public static MetricViewModel? Metric =>
        Dashboard is { } dashboard && dashboard.Providers[0].Metrics.Count > 0
            ? dashboard.Providers[0].Metrics[0]
            : null;

    /// <summary>The Settings page, populated.</summary>
    public static SettingsViewModel? Settings => Dashboard?.Settings;

    private static PopupViewModel BuildPopup()
    {
        if (_popup is not null)
        {
            return _popup;
        }

        var popup = new PopupViewModel(Providers(), TimeProvider.System, AltimSettings.Default);
        Fill(popup.Providers);
        popup.ApplySettings(AltimSettings.Default);
        _popup = popup;
        return popup;
    }

    private static DashboardViewModel BuildDashboard()
    {
        if (_dashboard is not null)
        {
            return _dashboard;
        }

        var dashboard = new DashboardViewModel(
            Providers(),
            new DesignHistoryService(),
            new DesignSettingsStore(),
            TimeProvider.System);

        Fill(dashboard.Providers);
        _dashboard = dashboard;
        return dashboard;
    }

    private static void Fill(IEnumerable<ProviderViewModel> rows)
    {
        foreach (ProviderViewModel row in rows)
        {
            row.Apply(UsageFor(row.Id));
            _ = row.LoadSessionsAsync(CancellationToken.None);
        }
    }

    private static IReadOnlyList<IUsageProvider> Providers() =>
    [
        new DesignUsageProvider(ProviderIdentity.ClaudeId, "Claude Code"),
        new DesignUsageProvider(ProviderIdentity.CodexId, "Codex"),
        new DesignUsageProvider("gemini", "Gemini CLI"),
    ];

    private static ProviderUsage UsageFor(string providerId)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        if (ProviderIdentity.IsAnthropic(providerId))
        {
            return new ProviderUsage(
                providerId,
                ProviderStatus.Active,
                [
                    new UsageMetric(
                        "five_hour",
                        "Session",
                        62d,
                        new LimitWindow(TimeSpan.FromHours(5), now.AddMinutes(134)),
                        MetricConfidence.Documented),
                    new UsageMetric(
                        "seven_day",
                        "Weekly",
                        38d,
                        new LimitWindow(TimeSpan.FromDays(7), now.AddDays(3)),
                        MetricConfidence.Documented),
                ],
                new TokenTotals(56_200, 12_800, 240_000, 9_000),
                now.AddSeconds(-40),
                null);
        }

        if (ProviderIdentity.IsOpenAI(providerId))
        {
            return new ProviderUsage(
                providerId,
                ProviderStatus.Idle,
                [
                    new UsageMetric(
                        "codex:300",
                        "5 hour",
                        41d,
                        new LimitWindow(TimeSpan.FromHours(5), now.AddMinutes(65)),
                        MetricConfidence.Documented),
                    new UsageMetric(
                        "codex:10080",
                        "Weekly",
                        null,
                        new LimitWindow(TimeSpan.FromDays(7), null),
                        MetricConfidence.BestEffort),
                ],
                null,
                now.AddMinutes(-2),
                null);
        }

        return new ProviderUsage(
            providerId,
            ProviderStatus.Error,
            [],
            null,
            now.AddMinutes(-11),
            "The command exited with code 1.");
    }

    private sealed class DesignUsageProvider : IUsageProvider
    {
        public DesignUsageProvider(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        // The design data never changes, so there is nothing to raise. Empty accessors are
        // what keep the contract satisfied without an event nobody subscribes to.
        public event EventHandler<ProviderUsage>? UsageChanged
        {
            add { }
            remove { }
        }

        public string Id { get; }

        public string DisplayName { get; }

        public ProviderStatus Status => ProviderStatus.Detected;

        public ValueTask<ProviderUsage> GetUsageAsync(CancellationToken ct) =>
            ValueTask.FromResult(UsageFor(Id));

        public ValueTask<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken ct)
        {
            if (!ProviderIdentity.IsAnthropic(Id))
            {
                return ValueTask.FromResult<IReadOnlyList<AgentSession>>([]);
            }

            DateTimeOffset now = DateTimeOffset.Now;
            return ValueTask.FromResult<IReadOnlyList<AgentSession>>(
            [
                new AgentSession(
                    "design-1",
                    Id,
                    now.AddMinutes(-42),
                    now.AddSeconds(-20),
                    new TokenTotals(18_400, 4_100, null, null),
                    "claude-opus-4",
                    true),
                new AgentSession(
                    "design-2",
                    Id,
                    now.AddHours(-3),
                    now.AddHours(-2),
                    new TokenTotals(6_200, 900, null, null),
                    "claude-sonnet-4",
                    false),
            ]);
        }

        public ValueTask RefreshAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class DesignHistoryService : IUsageHistoryService
    {
        public ValueTask RecordAsync(ProviderUsage usage, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<UsageSample>> GetRangeAsync(
            string providerId,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken ct)
        {
            if (!ProviderIdentity.IsAnthropic(providerId) && !ProviderIdentity.IsOpenAI(providerId))
            {
                return ValueTask.FromResult<IReadOnlyList<UsageSample>>([]);
            }

            List<UsageSample> samples = [];
            double level = ProviderIdentity.IsAnthropic(providerId) ? 8d : 4d;
            TimeSpan step = (to - from) / 20d;

            for (int i = 0; i < 20; i++)
            {
                level = Math.Min(96d, level + (i % 4 == 0 ? 11d : 3d));
                samples.Add(new UsageSample(
                    providerId,
                    "five_hour",
                    from + (step * i),
                    level,
                    TimeSpan.FromHours(5),
                    null,
                    null));
            }

            return ValueTask.FromResult<IReadOnlyList<UsageSample>>(samples);
        }

        // The carry-in: the last sample before the range, which is what draws the left edge
        // of a chart whose range holds no sample because nothing changed in it.
        public ValueTask<IReadOnlyList<UsageSample>> GetLatestBeforeAsync(
            string providerId,
            DateTimeOffset at,
            CancellationToken ct)
        {
            if (!ProviderIdentity.IsAnthropic(providerId) && !ProviderIdentity.IsOpenAI(providerId))
            {
                return ValueTask.FromResult<IReadOnlyList<UsageSample>>([]);
            }

            return ValueTask.FromResult<IReadOnlyList<UsageSample>>(
            [
                new UsageSample(
                    providerId,
                    "five_hour",
                    at.AddMinutes(-20),
                    ProviderIdentity.IsAnthropic(providerId) ? 6d : 3d,
                    TimeSpan.FromHours(5),
                    at.AddHours(4),
                    null),
            ]);
        }

        // No days yet: the designer draws unknown squares until the map view model lands and
        // this can hand it a shaped year.
        public ValueTask<IReadOnlyList<UsageDay>> GetDaysAsync(
            string providerId,
            DateOnly from,
            DateOnly to,
            CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<UsageDay>>([]);

        public ValueTask UpsertDaysAsync(IReadOnlyList<UsageDay> days, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask ClearAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class DesignSettingsStore : ISettingsStore
    {
        public ValueTask<AltimSettings> GetAsync(CancellationToken ct) =>
            ValueTask.FromResult(AltimSettings.Default);

        public ValueTask SaveAsync(AltimSettings settings, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
