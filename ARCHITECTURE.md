# Architecture

Altim is a background desktop utility: a tray/menu-bar presence, a fast popup panel, and a
dashboard window that only exists while it is open. Application logic is shared; anything the OS
does differently gets a thin native implementation behind an interface.

## Platform baseline

| Choice | Value | Why |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | .NET 8 and 9 both leave support on 2026-11-10 |
| UI | **Avalonia 12.1.2** | Linux StatusNotifierItem defect fixed in 12.1.0+; `ShowActivated=false` works on X11 from 12; xUnit v3 headless support is 12-only; 12.1.2 fixes a theme-change stall that cost 350–450ms per popup |
| MVVM | **CommunityToolkit.Mvvm 8.4.2** | template default, source-generated, AOT-safe. ReactiveUI is out: `Avalonia.ReactiveUI` is deprecated with no 12.x |
| Base theme | **Avalonia.Themes.Simple** + our own `ControlTheme`s | Fluent's identity is the thing we are avoiding |
| Font | **Avalonia.Fonts.Inter 12.1.2** | one bundled family, consistent across platforms |
| Database | **Microsoft.Data.Sqlite** | no ORM; hand-written SQL and migrations |
| Charts | **none** — custom `Control` with `Render(DrawingContext)` | no charting library has a stable Avalonia 12 release |
| Tests | **xUnit v3** + `Avalonia.Headless.XUnit 12.1.2` | the headless package is xUnit v3 from Avalonia 12 |
| Updates/packaging | **Velopack 1.2.0** (Windows, macOS), **nfpm** + AppImage (Linux) | maintained; Squirrel is dead |

Compiled bindings are on by default. Every project sets `IsAotCompatible=true`; the app publishes
with `PublishAot` in Release on Windows first, with the other platforms following once verified.

## Projects

```
Altim.Core                 models, interfaces, aggregation, reset math, thresholds, scheduler
Altim.Providers           provider contracts + shared JSONL/process helpers
Altim.Providers.Claude     Claude Code integration
Altim.Providers.Codex      OpenAI Codex integration
Altim.Storage              SQLite: schema, migrations, history, settings
Altim.UI                   Avalonia views, view models, design system, custom controls
Altim.Platform.Windows     Shell_NotifyIcon host, AppNotification, Run key, power events
Altim.Platform.MacOS       NSStatusItem interop, UNUserNotificationCenter, SMAppService
Altim.Platform.Linux       StatusNotifierItem, org.freedesktop.Notifications, XDG autostart
Altim.App                  composition root, DI, lifetime, tray wiring
tests/Altim.Core.Tests     ...Storage.Tests, ...Providers.Tests, ...UI.Tests
```

Dependency direction is one-way: `App → UI → Core`, `App → Platform.* → Core`,
`App → Providers.* → Providers → Core`, `Storage → Core`. Nothing depends on `App`. `Core` depends
on nothing but the BCL, which is what keeps the business logic testable.

Adding a provider means adding one project that implements `IUsageProvider` and registering it. No
UI change: views render whatever metrics a provider reports.

## Contracts

These signatures are fixed; implementations are written against them.

```csharp
public interface IUsageProvider
{
    string Id { get; }                       // "claude", "codex"
    string DisplayName { get; }
    ProviderStatus Status { get; }
    ValueTask<ProviderUsage> GetUsageAsync(CancellationToken ct);
    ValueTask<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken ct);
    ValueTask RefreshAsync(CancellationToken ct);
    event EventHandler<ProviderUsage>? UsageChanged;
}

public interface IUsageHistoryService
{
    ValueTask RecordAsync(ProviderUsage usage, CancellationToken ct);
    ValueTask<IReadOnlyList<UsageSample>> GetRangeAsync(string providerId, DateTimeOffset from,
                                                        DateTimeOffset to, CancellationToken ct);
    // Carry-in for a chart's left edge: rows are written only on change, so an empty
    // range means nothing moved, not that nothing is known.
    ValueTask<IReadOnlyList<UsageSample>> GetLatestBeforeAsync(string providerId,
                                                               DateTimeOffset at,
                                                               CancellationToken ct);
    ValueTask ClearAsync(CancellationToken ct);
}

public interface IPlatformService      // tray host, screen geometry, theme, power
{
    ITrayHost Tray { get; }
    ValueTask<PixelRect?> GetTrayAnchorAsync();
    event EventHandler? SystemResumed;
    event EventHandler? ThemeChanged;
}

public interface INotificationService  { ValueTask ShowAsync(Notification n, CancellationToken ct); }
public interface IAutoStartService     { ValueTask<bool> IsEnabledAsync(); ValueTask SetAsync(bool on); }
public interface IProcessMonitor       { ValueTask<IReadOnlyList<DetectedProcess>> ScanAsync(CancellationToken ct); }
```

`ProviderUsage` is deliberately open-ended, because providers do not expose the same metrics:

```csharp
public sealed record ProviderUsage(
    string ProviderId,
    ProviderStatus Status,
    IReadOnlyList<UsageMetric> Metrics,   // whatever this provider actually reports
    TokenTotals? Tokens,                  // null when not reported
    DateTimeOffset? LastRefreshed,
    string? StatusDetail);                // e.g. "Unable to retrieve usage"

public sealed record UsageMetric(
    string Key,                 // "five_hour", "seven_day", "codex:10080"
    string Label,               // "Session", "Weekly", "5 hour"
    double? UsedPercent,        // null = not reported
    LimitWindow? Window,        // length + reset instant
    MetricConfidence Confidence);
```

**A null is a null.** No metric is ever defaulted to zero, and the UI renders "Not reported by this
provider" for a null. `UsedPercent` above 101 is treated as unavailable — a known provider defect
returns a timestamp in that field.

## Monitoring

One scheduler owns all timing (`Altim.Core.Monitoring.MonitorScheduler`). Providers never start
their own timers, so the process has a single wake source and a single place to back off.

- **Event-driven first.** `FileSystemWatcher` on provider session directories, coalesced through a
  750ms debounce, drives refreshes. Filesystem events are hints, not data.
- **Polling is the floor, not the plan.** A `PeriodicTimer` at 60s covers anything watchers miss.
  Missed ticks coalesce, so a laptop waking from sleep produces one refresh, not a backlog.
- **Network-touching calls are rate-limited separately** and never run faster than once a minute.
- The scheduler pauses on suspend, resumes on wake, and refreshes once on resume.
- While the popup or dashboard is open, the cadence tightens to 10s; on close it relaxes again.

Provider work happens off the UI thread and returns immutable records. Failures are contained per
provider: a thrown exception becomes `ProviderStatus.Error` with a message, never a crash.

## Reading provider data without hurting the machine

The evidence is in [PROVIDERS.md](PROVIDERS.md). The rules that shape the code:

- Never full-scan session directories. The local stores measured 28.3 GB and 1.3 GB. Candidate
  files come from a state database or modification time, and only the tail of a file is read.
- De-duplicate on message identity before summing tokens; naive summing overcounted by 3.15×.
- Include subagent transcripts, which accounted for 5× the parent session's tokens.
- Parse only numeric and timestamp fields. Every other field is discarded inside the reader.
- Classify limit windows by their length in minutes, never by slot name, and tolerate off-by-one
  values.

## Storage

One SQLite database in the platform config directory, opened with WAL.

```sql
CREATE TABLE schema_version (version INTEGER NOT NULL);
CREATE TABLE usage_sample (
  id INTEGER PRIMARY KEY,
  provider_id TEXT NOT NULL,
  metric_key  TEXT NOT NULL,
  captured_at INTEGER NOT NULL,          -- unix seconds, UTC
  used_percent REAL,                      -- nullable: unknown stays unknown
  window_minutes INTEGER,
  resets_at INTEGER,
  input_tokens INTEGER, output_tokens INTEGER,
  cache_read_tokens INTEGER, cache_write_tokens INTEGER
);
CREATE INDEX ix_usage_sample_lookup ON usage_sample (provider_id, metric_key, captured_at);
CREATE TABLE setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE notification_state (
  provider_id TEXT NOT NULL, metric_key TEXT NOT NULL, threshold INTEGER NOT NULL,
  fired_at INTEGER NOT NULL, window_resets_at INTEGER,
  PRIMARY KEY (provider_id, metric_key, threshold)
);
```

No project names, prompts, commands or file paths are stored. Samples are written only when a value
changes, and are down-sampled after 30 days to hourly rows: each hour keeps its **peak** percentage,
and takes its token counters, window length and reset instant from the hour's **last** row, because
token counts are cumulative totals rather than deltas and summing them would multiply the same
total by however many times it was observed. A reading whose provider is in `Error` is not stored at
all. Migrations are sequential, forward-only, each in its own transaction, and each re-checks the
recorded version inside that transaction, so two instances starting at once — autostart plus a
manual launch — cannot apply the same rung twice.

## Notifications

Thresholds are evaluated in `Core` and are pure functions over `(usage, settings, state)`, which is
what makes them testable. A notification fires when a metric crosses a configured threshold upward;
`notification_state` records it so it cannot fire twice for the same window. When the window's
`resets_at` passes, the state is cleared and a reset notification may fire once. No timers, no
spam, no notifications during the first refresh after start.

## UI

`Altim.UI` holds views, view models and the design system from [docs/DESIGN.md](docs/DESIGN.md).

- `Themes/Tokens.axaml` — colours per theme variant, consumed with `DynamicResource`
- `Themes/Primitives.axaml` — radii, spacing, durations, type scale (variant-invariant)
- `Themes/Controls/*.axaml` — one `ControlTheme` per control we restyle
- `Controls/Meter.cs`, `Controls/UsageTape.cs` — custom render controls, no dependency

Two windows:

1. **Popup panel** — created once at startup and hidden, never closed. This is deliberate: a
   tray-only process cannot reach screen geometry without a live top-level, and Windows only
   invalidates its screen cache from a live window, so a closed-window app opens menus on
   disconnected monitors. Borderless, `SizeToContent`, topmost, not in taskbar, closes on deactivate.
2. **Dashboard** — constructed on demand and closed on dismiss, so its render surfaces are released.

Positioning uses a three-tier anchor: exact tray rect where the OS gives one, cursor position at
click time, then the working-area corner. Screen coordinates are physical pixels while window size
is in device-independent units, so all arithmetic uses the *target* screen's scaling — including
margins — and clamps to the working area. Positioning happens after layout, not before `Show()`.

## Platform matrix

| Concern | Windows | macOS | Linux |
|---|---|---|---|
| Tray host | own `Shell_NotifyIcon` message-only window, version 4 (click coordinates), `Shell_NotifyIconGetRect` | own `NSStatusItem` via `objc_msgSend`; anchor from the status button's window frame | Avalonia `TrayIcon` (StatusNotifierItem) |
| Popup | positioned borderless window | positioned borderless window | working-area corner; no anchor exists in the protocol |
| Notifications | `Microsoft.WindowsAppSDK` `AppNotificationManager` | `UNUserNotificationCenter` interop | `org.freedesktop.Notifications` over `Tmds.DBus.Protocol` |
| Autostart | `HKCU\...\Run`, **reporting state from `StartupApproved`** | `SMAppService.mainApp` | `~/.config/autostart/*.desktop`, disable via `Hidden=true` |
| Power/session | `Microsoft.Win32.SystemEvents` | `NSWorkspace.shared.notificationCenter` (not the default centre) | logind `PrepareForSleep` |
| Theme | `ColorValuesChanged` | same, plus template tray icon | portal `org.freedesktop.appearance`; may resolve late |

macOS runs as an accessory app: `MacOSPlatformOptions.ShowInDock = false` **and** `LSUIElement` in
the bundle, because Avalonia sets the activation policy at runtime and overrides the plist alone.
The macOS tray icon is a template image; menu-bar appearance follows the wallpaper, so Altim does
not swap it on theme change.

Known platform behaviours the code must handle: the macOS popup receives a spurious deactivation
when an in-app popup takes key focus, so the handler verifies the new key window; on Windows,
clicking the tray deactivates the panel and would immediately reopen it, so deactivation is ignored
while the foreground window is the shell's tray window; Windows 11 hides new tray icons in the
overflow by default, which first-run onboarding explains rather than tries to defeat.

Linux uses the X11 backend, including under Wayland via XWayland. Avalonia's Wayland backend is
experimental and its positioning, topmost and activation calls are no-ops, and Wayland has no
systray protocol at all.

## Performance targets

Measured and published in the README; they are budgets, not aspirations.

| Metric | Budget |
|---|---|
| Cold start to tray icon visible | < 800ms |
| Popup open (already warm) | < 100ms |
| Idle CPU | < 0.1% average |
| Idle working set | < 80MB |
| Database growth | < 5MB/year at default cadence |

Techniques: no `MainWindow`, `ShutdownMode.OnExplicitShutdown`, lazy dashboard, workstation
non-concurrent GC with `ConserveMemory=5` and 1MB regions, `PeriodicTimer` over `DispatcherTimer`
for background work, diagnostics excluded from Release.

## Testing

Business logic is tested without a UI: reset arithmetic, percentage normalisation, provider
normalisation across both schema dialects, token de-duplication, threshold crossing and
de-duplication, settings round-trips, migrations, and scheduler behaviour under simulated clocks.
Provider readers are tested against captured fixture files containing synthetic data only — no real
transcripts enter the repository. View models are plain objects and are tested directly; headless
Avalonia tests cover the meter and tape controls.

CI runs on 4-core runners with `--blame-hang-timeout`, because the xUnit host is known to deadlock
on 2-core hosts.

## Risks

1. **macOS interop is written without a macOS host to test on.** It is isolated behind `ITrayHost`
   with a documented fallback to Avalonia's tray icon plus a native menu, and is marked unverified
   until someone runs it.
2. **Provider formats are internal and disclaimed by their vendors.** Every field is optional at the
   parse boundary; a shape change degrades one metric to unavailable instead of breaking the app.
3. **The live Codex quota call requires the vendor CLI and the network**, so it is optional, rate
   limited, and falls back to local files.
4. **Native AOT for Avalonia is officially supported but rarely shipped.** It is a Release-only
   Windows setting first, behind a verification step, and can be turned off without code changes.
5. **Status line integration writes to a user configuration file.** It is opt-in, merges rather than
   overwrites, refuses to replace an existing status line, and offers revert.
