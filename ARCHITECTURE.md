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
Altim.UI                   Avalonia views, view models, design system, custom controls, popup geometry
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

## The process has two entry points

`Program.Main` branches on its first statement, and the two branches share nothing but the
executable:

```
Altim.exe                        the application: packaging hooks, single instance, Avalonia, tray
Altim.exe altim-statusline       the status-line command Claude Code runs
```

The second is what makes reset times reachable at all. Claude Code publishes them only to a
configured status-line command: it writes a JSON payload to the command's standard input and
renders the command's standard output. So Altim registers *itself* as that command, and the branch
reads the payload, writes the numbers to `<claude config>/altim-statusline.json`, prints one short
line, and returns. `ClaudeStatusLineHelper` is the whole of it and
`ClaudeStatusLineReader` reads the file back on the next refresh.

Three constraints shape it, and each one is load-bearing:

- **It must return in well under 100ms.** Claude Code debounces at 300ms and cancels an in-flight
  command when a newer update arrives. Measured on Windows 11 26200: 18 to 24ms for the shipped
  Native AOT build, 67 to 78ms for a framework-dependent `dotnet build`. Most of that is process
  start, which is why the branch is ahead of `VelopackApp`, ahead of the single-instance guard and
  ahead of every Avalonia type, with the rest of `Main` moved into a `NoInlining` method so the
  helper does not pay to have those assemblies resolved.
- **It writes numbers and nothing else.** The payload carries the working directory, the project
  directory, the transcript path, a session id and the model. The serialiser can only write numbers
  and instants, so there is no path through it a string could take. `PRIVACY.md` is the contract and
  `ClaudeStatusLineHelperTests` asserts it over the bytes on disk.
- **The file is swapped in, not rewritten.** `File.Replace`, not `File.Move(overwrite: true)`.
  Measured on Windows 11 26200: with the state file open by a reader using the sharing
  `ClaudeStatusLineReader` asks for, the move is refused outright and leaves both the stale file and
  the temporary one on disk, while the replace succeeds and the reader's handle keeps reading the
  file it opened. Altim's own provider opens that file on every refresh tick, so the move would have
  dropped whichever write it collided with.

The single-instance guard is deliberately not in this path. Two Claude Code sessions run the helper
at the same time, and a guard would make one of them exit without writing.

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
    event EventHandler? SystemSuspending;   // pause the scheduler; may not arrive
    event EventHandler? SystemResumed;      // resume it and refresh once
    event EventHandler? ThemeChanged;
}

public interface INotificationService  { ValueTask ShowAsync(Notification n, CancellationToken ct); }
public interface IAutoStartService     { ValueTask<bool> IsEnabledAsync(); ValueTask SetAsync(bool on); }

// Claude Code's statusLine setting, from Altim's side. Both calls answer with the state the
// settings file is actually in, never with the outcome of the request: install, not installed,
// a status line of the user's own in the way, no Claude Code here, or a file that would not
// be read. Implemented in the composition root over StatusLineInstaller.
public interface IStatusLineService
{
    ValueTask<StatusLineInstallState> InspectAsync(CancellationToken ct);   // a dry run; writes nothing
    ValueTask<StatusLineInstallState> SetAsync(bool install, CancellationToken ct);
}
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
- **Anything that starts a process has a floor of its own**, separately from the refresh that
  asked for it. A refresh is file reads and is cheap enough to run on a filesystem event; a
  process launch is not. Claude Code's agents listing ran on every refresh, and it is the most
  expensive thing Altim does: measured here, one `claude agents --json` starts **105 processes
  and spends 5.6 seconds of CPU**, and takes 18–27 seconds of wall clock, which is longer than
  its own 15-second budget — so it is usually killed part way through and the reading falls back
  to the process scan. Tied to a filesystem event that was 173 child processes a minute and
  17.8% of one core. It now runs at the polling floor, and two things push it out to five
  minutes: a process scan that sees nothing that looks like an agent, and a previous attempt
  that did not answer. Neither costs a status — liveness comes from the scan on every refresh —
  and what they delay is how soon a new session appears by name in the activity list.
- **Network-touching calls are rate-limited separately** and never run faster than once a minute.
  They are also permission-gated: `INetworkPolicy` is read at the moment of the call rather than
  copied into a provider's options, because a provider is built once and outlives every settings
  change, and a permission frozen at start-up goes on calling the vendor until the next restart.
- The scheduler pauses on `SystemSuspending`, resumes on `SystemResumed`, and refreshes once on
  resume. **A wake with no suspend before it is normal**, not a fault: a modern standby machine
  can sleep without sending the classic broadcast. The composition root enters the suspended
  state itself in that case, so the "refresh on resume" setting holds either way.
  **In one case the wake costs two reads rather than one**: a poll tick that came due while the
  scheduler was suspended is signalled whether or not the loop was there to skip it, so an
  unconsumed one is taken up the moment resume clears the flag. It costs a single extra read and
  settles itself, and it is written down here rather than rounded off in the paragraph above.
- **The display going off is not a suspend**, though the display coming on *is* treated as a
  wake. The asymmetry is deliberate: an extra wake costs one refresh, while a wrong suspend
  stops recording an agent that is working against a dark monitor.
- While the popup or dashboard is open, the cadence tightens to 10s; on close it relaxes again.
- **The scheduler's cadences are fixed at construction, so changing Refresh replaces it.**
  Everything that produces work for it therefore holds a `SchedulerHandle` rather than an
  instance: the filesystem watchers, and the first-readings pass, which also follows a rebuild
  that lands while it is running. A hint delivered to a disposed scheduler is documented not to
  throw, so a watcher left holding the old one produced no error, no log line and no refresh —
  it simply stopped being event-driven until the next restart.

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
CREATE TABLE usage_day (
  provider_id        TEXT    NOT NULL,
  day                TEXT    NOT NULL,   -- local calendar day, ISO yyyy-mm-dd
  input_tokens       INTEGER,
  output_tokens      INTEGER,
  cache_read_tokens  INTEGER,
  cache_write_tokens INTEGER,
  peak_percent       REAL,               -- nullable: unknown stays unknown
  source             TEXT    NOT NULL,   -- 'observed' | 'backfilled'
  updated_at         INTEGER NOT NULL,
  PRIMARY KEY (provider_id, day)
);
```

The write-ahead log is bounded, which it is not by default. SQLite checkpoints automatically at
1000 pages but does not shrink the log afterwards — it rewinds and overwrites the same bytes — so
the file settles at whatever high-water mark it ever reached and stays there: measured at 3.9 MB
in front of a 240 KB database. Altim checkpoints at 256 pages, sets `journal_size_limit` **after**
entering WAL mode so a reset truncates rather than rewinds, and runs a `TRUNCATE` checkpoint from
the maintenance pass once nothing has written for two minutes.

**"Nothing has written" has to mean what it says, and for a long time it did not.** The clock was
stamped when the writer was *taken*, and taking the writer to find out there is nothing to do is
something Altim does constantly: the history service, the notification state, the down-sampler and
the vacuum check all do it, and the maintenance pass runs two of them in the same method that then
asks the question. The answer was always "something has written", so the checkpoint was unreachable
by construction — measured over nine minutes with both provider stores empty and nothing to report,
it never ran once. A lease now stamps the clock only when it changed a row, which is asked of
SQLite's own `total_changes()` rather than of each caller's good intentions; and the two callers
that were opening a write transaction to discover they had nothing to write now compare first. None of that trades durability:
`synchronous` stays `NORMAL`, a checkpoint only moves already-committed frames and flushes before
resetting, and a process that is killed still has its writes replayed from the log on the next
open.

No project names, prompts, commands or file paths are stored. Samples are written only when a value
changes, and are down-sampled after 30 days to hourly rows: each hour keeps its **peak** percentage,
and takes its token counters, window length and reset instant from the hour's **last** row, because
token counts are cumulative totals rather than deltas and summing them would multiply the same
total by however many times it was observed. A reading whose provider is in `Error` is not stored at
all. Migrations are sequential, forward-only, each in its own transaction, and each re-checks the
recorded version inside that transaction, so two instances starting at once — autostart plus a
manual launch — cannot apply the same rung twice.

### The day table, and why its writers do not compete

`usage_day` holds one row per provider per **local** calendar day — the day resolved when the row
is written and stored as text, so a timezone move cannot re-bucket settled history. It is what the
usage map draws, it is kept indefinitely rather than down-sampled, and a day with no row is the
map's unknown square.

**Two writers share a row and neither owns the whole of it, so an upsert merges per field rather
than replacing.**

| Field | Written by | Rule |
|---|---|---|
| the four token columns, and `source` | the backfill only | a later backfill replaces an earlier one; a write carrying no tokens leaves them alone |
| `peak_percent` | the sample rollup only | the day's maximum, and a later write may raise it but never lower it |

`updated_at` moves only when one of those actually changed. That is not tidiness: the guard is what
keeps the write-ahead log truncatable. The maintenance pass re-offers yesterday and today every few
minutes, an unconditional rewrite would move SQLite's `total_changes()`, and that counter is exactly
what stamps the write clock `CheckpointIfIdleAsync` reads — so without it the log would never be
emptied again on a machine left switched on. The comparisons are `IS NOT`, never `<>`: the columns
are nullable, and a figure appearing where there was none has to count as a change.

**The rollup contributes the peak and never a token figure**, because a live reading's token totals
are a running total rather than a per-day amount: Claude Code publishes the cumulative sum of every
transcript its scanner has read, and Codex publishes a sum over whichever sessions were most
recently active, which moves in both directions between two reads a minute apart. A day's tokens
therefore come only from the providers' own per-day history, and `source` names where that figure
came from. A day with a peak and no tokens is drawn as unknown: how close to the limit the user came
is known, what they spent is not.

An earlier rule ranked whole rows — observed beat backfilled — and it is retired. It let the rollup,
which has no token figure to offer, overwrite correct per-day figures with running totals; migration
3 empties the token columns of every row that rule produced, since there is no arithmetic that turns
a running total back into a day. An install carrying version 1 was upgraded through 2 to 3 against a
live store to check that, and the rung is forward-only like the others.

**Both writers ride the maintenance pass, and neither has a timer of its own**, because the idle
cost is a measured budget and a second wake source would spend against it. The rollup covers
yesterday and today on every pass, reaches back to the last day this process rolled up so a machine
waking from a long sleep finishes the days it slept through, and is floored at 35 days so the first
pass after an upgrade cannot become an unbounded read. The backfill asks each provider that
implements `IUsageHistorySource` for a year of its own history, at most once a day, stamping the
last run per provider in the `setting` table so a source that cannot answer never holds back one
that can. A provider with no history source, an unreadable store or a failed call leaves its days
unknown, and the failure is logged as an exception type with no message, because a message from
inside a provider's store names the file it failed on.

**How far back a row reaches is the provider's answer, not Altim's request.** Each is asked for a
year and answers with what it still has: on the machine the feature was verified against, Codex
accounted for 100 days spread over about nine months while the transcript scan covered four, so one
row's left edge is far older than the other's and the difference is drawn as unknown rather than
levelled off. [PROVIDERS.md](PROVIDERS.md) has both figures and what each depends on.

Day rows are two a day here against thousands of samples, which is why they are kept whole while
samples are collapsed to an hour. The map is the long memory; the samples are the short one.

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
- `Controls/Meter.cs`, `Controls/UsageTape.cs`, `Controls/UsageMap.cs` — custom render controls, no dependency

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
It is `PopupPlacement` in `Altim.UI`, a pure function over rectangles with no window in it, which
is what lets a taskbar on each of the four edges be asserted without four displays.

The placement also owns the window's shadow inset, and returns it with the position. The window is
the panel plus that inset, the inset is transparent, and transparent window still hit tests — so on
whichever edge faces the tray icon it is trimmed back to the gap the panel already keeps from it.
See the Windows notes below for the defect that came from not doing this.

Two platform caveats the arithmetic has to respect. **macOS screen coordinates start at the bottom
left**, so a status item's frame is flipped before it reaches the shared placement code. **Linux
skips the middle tier entirely**: a StatusNotifierItem click arrives over the bus with no
coordinates at all, so there is no tray rectangle *and* no cursor position, and the panel goes
straight to the working-area corner.

## Platform matrix

| Concern | Windows | macOS | Linux |
|---|---|---|---|
| Tray host | own `Shell_NotifyIcon` message-only window, version 4 (click coordinates), `Shell_NotifyIconGetRect` | own `NSStatusItem` via `objc_msgSend`; anchor from the status button's window frame | Avalonia `TrayIcon` (StatusNotifierItem) |
| Popup | positioned borderless window | positioned borderless window | working-area corner; no anchor exists in the protocol |
| Notifications | `Microsoft.WindowsAppSDK` `AppNotificationManager` | `UNUserNotificationCenter` interop | `org.freedesktop.Notifications` over `Tmds.DBus.Protocol` |
| Autostart | `HKCU\...\Run`, **reporting state from `StartupApproved`** | `SMAppService` (the selector is `mainAppService`), macOS 13+, bundle required; only *enabled* reports true, "requires approval" does not | `~/.config/autostart/*.desktop`, disable via `Hidden=true`; the entry names `$APPIMAGE` when it is set, never the AppImage's temporary mount point |
| Power/session | `Microsoft.Win32.SystemEvents` plus `PBT_APMSUSPEND` / `GUID_CONSOLE_DISPLAY_STATE` | `NSWorkspace.shared.notificationCenter` (not the default centre): `WillSleep`, `DidWake`, `ScreensDidWake` | logind `PrepareForSleep` on the **system** bus — one signal, `true` to sleep and `false` to wake |
| Theme | `ColorValuesChanged` | **`NSDistributedNotificationCenter`** — appearance is *not* posted to the workspace centre | portal `org.freedesktop.appearance` on the **session** bus; may resolve late |

macOS uses **three** notification centres, and picking the wrong one fails silently rather than
loudly: the workspace centre for sleep and wake, the distributed centre for appearance, and the
user-notification centre for notifications.

macOS runs as an accessory app: `MacOSPlatformOptions.ShowInDock = false` **and** `LSUIElement` in
the bundle, because Avalonia sets the activation policy at runtime and overrides the plist alone.
The macOS tray icon is a template image; menu-bar appearance follows the wallpaper, so Altim does
not swap it on theme change.

Known platform behaviours the code must handle: the macOS popup receives a spurious deactivation
when an in-app popup takes key focus, so the handler verifies the new key window; on Windows,
clicking the tray deactivates the panel and would immediately reopen it, so a deactivation is
classified by the window that took the foreground rather than acted on blind — see the fifth
Windows detail below. **Windows 11 puts a new tray icon in the overflow by default.** Altim does
not try to defeat that: it reports no anchor while the icon is hidden, so the panel falls to the
next positioning tier rather than opening beside the chevron. There is no first-run onboarding and
nothing else tells the user where the icon went; this document promised onboarding that explained
it, and the promise is withdrawn rather than left standing over an empty space.

Six Windows details were established by measurement rather than documentation. The code depends on
all six; the third, the fifth and the sixth were shipped defects, and what they cost is why those
fixes are described rather than just applied.

- **Asking for the icon's rectangle does not fail while the icon sits in the overflow.** On
  Windows 11 26200 it succeeds and returns the *chevron's* rectangle, which is geometrically
  indistinguishable from a promoted icon. Altim therefore decides promotion from the shell's own
  per-icon record plus a hit test against the flyout window, and reports no anchor when the icon is
  hidden, so the panel falls to the next positioning tier instead of opening next to the chevron.
- **One left click produces three callbacks, in the wrong order.** A version 4 icon is
  documented to report primary activation as `NIN_SELECT` instead of the button messages.
  Windows 11 26200 sends `WM_LBUTTONDOWN`, `WM_LBUTTONUP` and *then* `NIN_SELECT`, inside four
  milliseconds — the message that is supposed to have been replaced arrives first. Right click
  is the same shape: `WM_RBUTTONDOWN`, `WM_RBUTTONUP`, then `WM_CONTEXTMENU`. `WindowsTrayHost`
  collapses the primary-activation family to one `Clicked` per press. The menu path needs no
  guard because Windows refuses a second `TrackPopupMenuEx` while one is already tracking, so
  the duplicate is inert — verified, rather than assumed.
- **Room reserved for a shadow is still window, and a window over a tray icon eats its clicks.**
  The window is larger than the visible panel so the shadow has room to fall, and the panel is
  positioned 8 DIPs above the icon while reserving 32 below itself, which put the window's bottom
  edge 24 DIPs *past* the icon's top. Measured with `WindowFromPoint` on a bottom taskbar at 100%:
  the panel window owned the pixels from y=1044 to y=1055 over an icon at y=1044, and the shell
  owned 1056 down — so a click on the lower half of the icon toggled the panel and a click on the
  upper half did nothing at all, which reads as a tray icon that does not work. **Fixed** by
  trimming the inset on the edge facing the anchor back to that 8 DIP gap: past the panel's near
  edge the taskbar covers that side's shadow anyway, so the only thing given up is room nothing
  could see, and the panel does not move. The window now ends at y=1032, the working area's own
  edge, and every row of the icon toggles. `PopupPlacement` returns the trimmed inset with the
  position and `PopupHost` applies it to the window — to the panel's margin *and* to the window's
  width, because `PART_PopupPanel` is a fixed 320 in a stretched slot and a window sized for the
  untrimmed inset centres the panel in the difference. The two alternatives were worse: moving the
  panel 24 DIPs further from the icon than DESIGN.md asks for is a visible change to pay for an
  invisible one, and answering `WM_NCHITTEST` with `HTTRANSPARENT` needs a window procedure hook
  Avalonia 12.1.2 does not expose — `Win32Properties` is internal.
- **There is no foreground window while activation is moving, and that is when the panel asks.**
  Clicking the tray icon makes the taskbar the foreground window and deactivates the panel, so
  closing on every deactivation closes the panel a moment before the same click reopens it. The
  guard against that answered "not a real deactivation" to any foreground window it could not
  identify, `NULL` included — and `NULL` is what `GetForegroundWindow` returns for the moment
  between one window losing activation and the next gaining it, which is exactly when the
  deactivation arrives. Measured over one application log: the panel closed by deactivation
  **once**, against fifty-six closes by clicking the tray icon again. DESIGN.md says the panel
  closes on deactivate and in practice it did not, so it sat over whatever the user had switched
  to. **Fixed** by making the unknown answer a question rather than a verdict: the foreground is
  classified as this process, the tray, another window, or not settled yet, and only the last one
  waits — re-read at 120ms, at most twice, then dismissed anyway because the panel is not the
  active window. Re-reading is free in the case the guard exists for, because the tray click has
  already toggled the panel shut by then and the re-check finds nothing to hide. The class list
  shrank at the same time: `Windows.UI.Core.CoreWindow` is every packaged application's window,
  not the shell's, so clicking Settings or Calculator read as clicking the tray. The rule is
  `PopupDismissal` in `Altim.UI`, beside `PopupPlacement` and for the same reason — it is a pure
  function over three facts, so it is asserted without a screen.

  **A deactivation is not guaranteed to arrive at all**, which the rule above cannot help with
  because it is only ever consulted by one. Two reproduced ways to have a panel nothing dismisses:
  a double click on the icon, where the second press is inside the host's 200ms de-duplication
  window so it raises no toggle, and the deactivation it *does* cause lands inside the panel's
  250ms reopen guard; and the second-launch surfacing, where the running instance cannot take the
  foreground and so the panel opens without ever being active. Both leave a topmost panel over the
  user's work with no way to put it down. Three changes, and each covers a different half:
  a second launch hands its foreground right over with `AllowSetForegroundWindow` before it
  signals, so the panel can activate; a deactivation inside the reopen guard is re-asked once the
  guard is over rather than dropped; and while the panel is visible it watches, at 250ms, for the
  foreground *changing* and for a click landing outside it. The watch is a change rather than a
  state on purpose: a panel surfaced over a window that already held the foreground would
  otherwise dismiss itself a quarter of a second after the user asked for it. The click is read
  with `GetAsyncKeyState`, whose "pressed since you last asked" bit is what lets a quarter-second
  poll catch a click that moved no foreground at all — which is exactly the case, because the
  window it landed on already had it. "One of ours" is two answers now: the panel and anything it
  owns is an overlay and keeps it open, while the dashboard is a window the user switched to and
  dismisses it.

- **The session-end query is a question, and answering it wrongly blocks sign-out.** Avalonia
  raises `ShutdownRequested` from `WM_QUERYENDSESSION` and asks every window to close while it is
  answering; whether a window cancelled that close *is* the answer Windows gets back. The panel is
  hidden and never closed, so it cancelled its own close unconditionally, and the query therefore
  returned a veto — measured by sending the query to the running process, which returned 0 — while
  the handler for the query had already removed the tray icon and latched the shutdown flag. The
  result was an Altim with no icon, still running, whose Quit no longer did anything: a ghost until
  Task Manager. **Fixed** three ways: `PopupCloseRule` lets a close through when its reason is an
  application or OS shutdown and hides the panel for every other reason; nothing irreversible
  happens on the query, only a log line; and the teardown runs from the lifetime's `Exit`, which is
  the point at which the framework has decided to go. The latch moved with it, so a session end
  that is cancelled leaves Quit working. The teardown is synchronous because the thread it runs on
  is the dispatcher's and there is nothing behind it — which also means the windows have to be
  closed before the first `await` hands the rest to the thread pool, or a pool thread ends up
  waiting for a dispatcher frame that cannot run.

- **A broadcast cannot reach a message-only window.** The icon lives on the message-only window as
  intended, but Explorer's restart notice is a broadcast, so a second never-shown top-level window
  receives it, owns the native menu (bringing a menu to the foreground needs a top-level owner) and
  receives power-state notifications. Classic resume events do not cover modern standby, so display
  state off→on is also treated as a wake, coalesced so one wake raises one event.

Linux uses the X11 backend, including under Wayland via XWayland. Avalonia's Wayland backend is
experimental and its positioning, topmost and activation calls are no-ops, and Wayland has no
systray protocol at all.

## Performance targets

Measured and published in the README; they are budgets, not aspirations. **They apply to the
shipping build**, which is the Native AOT publish — see below for why that distinction is load
bearing rather than a let-out.

| Metric | Budget | Measured |
|---|---|---|
| Cold start to tray icon visible | < 800ms | 260ms |
| Popup open (already warm) | < 100ms | 8.4ms first open, under 1ms after |
| CPU, Altim and its children | < 5% of one core idle | 3.1% idle, 10.0% while an agent writes |
| Idle working set | **< 120MB** | 107MB |
| Database growth | < 5MB/year at default cadence | on track; see the README |

Techniques: no `MainWindow`, `ShutdownMode.OnExplicitShutdown`, lazy dashboard, workstation
non-concurrent GC with `ConserveMemory=5` and 1MB regions, `PeriodicTimer` over `DispatcherTimer`
for background work, diagnostics excluded from Release.

The CPU budget used to read "< 0.1% average" with no denominator, which is not a property of
the program: the same binary doing the same work passes it on a sixteen-core machine and fails
it on a four-core one. It is now a share of one core, which is both machine-independent and the
thing that actually costs a battery.

**It also used to count the wrong processes, and that was the larger error of the two.** The
figure came from `Process.TotalProcessorTime` on Altim itself, and Altim reads two of its sources
by running the vendor's own command line — processes that live for a second or two and are gone
before anything could sample them. Measured with Altim inside a job object, so that the CPU of
every process that has ever been in the job is counted whether or not it has exited: **23.2% of
one core and 259 child processes a minute** while an agent was writing transcripts, against a
published 2.4%. Gating the agents listing takes that to **10.0% and 61 processes a minute**; idle
is **3.1%**, which the gating does not move because the polling floor already held the listing to
once a minute there. Altim's own share is 2.7% under load and about 1.8% idle, which is what a
process holding a live hidden window costs before it does any work of its own.

The budget follows the honest number rather than the other way round: it is now under 5% of one
core at idle, and the load figure is published beside it rather than folded into it. What is left
is mostly not Altim — it is the provider command lines, one of which starts 105 processes and
spends 5.6 seconds of CPU to answer a question about which sessions are running.

**The CPU row is the one figure in the table not taken from the shipping build.** It was measured
on the framework-dependent build, and it is published as that rather than restated as a number
nobody measured. The part ahead-of-time compilation moves is Altim's own share — 2.7% under load
and 1.8% at idle — and the rest is the provider command lines, which are the same processes
whichever way Altim itself was compiled.

### The working-set budget was wrong, and this is where the memory goes

It was 80MB. Nothing built has ever met it, and the number was an aspiration written before
anything ran. It has been replaced with a budget the product meets, and the measurements that
set it are recorded here so the next person does not have to take the number on trust.

Measured on Windows 11 26200, 16 cores, NVIDIA discrete graphics, against the real provider
stores on that machine (28.3GB of Codex rollouts, 1.3GB of Claude transcripts), four minutes
after start:

| Build | Working set | Private | Threads |
|---|---|---|---|
| Framework-dependent `dotnet build` | 156MB | 105MB | 29 |
| **Native AOT publish — what ships** | **107MB** | 99MB | 26 |
| Native AOT, both provider stores empty | 89MB | 78MB | 25 |

Where the 107MB is, from a walk of the process's committed regions cross-referenced against its
resident pages:

| | Working set | Committed |
|---|---|---|
| Image (mapped executables) | 55MB | 289MB reserved address space |
| Private | 49MB | 63MB |
| Other file- and pagefile-backed sections | 3MB | 58MB |

- **55MB of image pages.** 14MB of it is Altim's own AOT binary; the rest is Skia, ICU,
  HarfBuzz, SQLite, DirectWrite and — 10.5MB of it — the NVIDIA user-mode driver, most of that
  shared with every other process that has it mapped. The framework-dependent build spends
  another 18MB here on `System.Private.CoreLib`, `coreclr`, `clrjit` and 77 managed assemblies,
  which is what AOT removes.
- **49MB private.** The managed heap is **7MB live** against 34MB committed; the rest is runtime
  structures, native allocators and thread stacks.

Committed is much larger than resident in every row, which is the normal shape of a reserved
address space rather than memory anyone is paying for.

Four candidate explanations were tested. Three are not the cause and one is:

- **The hidden popup window is not it.** Never priming the window at all saved 2.7MB. Avalonia
  brings its rendering stack up whether or not a top level exists, so keeping the panel alive —
  which ARCHITECTURE.md requires, because screen geometry is unreachable without it — costs
  almost nothing. This was the most likely suspect and it is wrong.
- **The provider caches are not the bulk of it, but they are not nothing.** Pointing both
  providers at empty stores measures 89MB against 107MB, so the real 28.3GB and 1.3GB stores
  are worth about 18MB of working set. Almost none of that is managed: the live heap is 7MB
  either way, which is the incremental scanner working as designed. It is the pages touched
  reading file tails plus the message-identity set that has to outlive a pass, and it is the
  price of reading the tails rather than the files.
- **The GC configuration is not it.** An aggressive compacting gen2 collection with LOH
  compaction, run after the expensive first scan, returned 1.2MB.

**The fourth is the answer: the GPU rendering stack.** Running Avalonia with
`Win32RenderingMode.Software` — no D3D11, no DXGI, no ANGLE, no vendor user-mode driver —
measures **80MB working set, 49MB private and 13 threads**, against 107MB, 99MB and 26. That is
the whole of the gap, and it closes it exactly.

**It has not been taken, and the reason is not inertia.** On this machine the trade looks free:
the popup is pixel-identical between the two modes, including its shadow and transparency, the
dashboard renders correctly including the history chart, and the first popup open is 8.4ms
against 9.1ms. What cannot be tested here is the case the change would actually hurt — software
rasterisation costs pixels, so a 1400x900 dashboard being resized on a 4K display is several
megapixels per frame on the CPU where it is currently free. Choosing the rendering backend for
the whole application on the evidence of one 1080p display with a discrete GPU is not a trade to
make silently. The measurement is recorded here so it can be made deliberately, and it is one
line in `Program.cs` when it is.

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

1. **Windows notifications carry a deployment dependency.** The notification API needs the Windows
   App Runtime. Framework-dependent registration failed on a clean machine because the runtime's
   main package was absent, and the self-contained payload for the pinned version omits a resource
   library that registration requires. Packaging must resolve this, and notification failure must
   degrade to "Altim runs and does not notify", never to a failed start.
1. **macOS and Linux interop are written without a macOS or Linux host to test on.** Both are
   isolated behind `IPlatformService` and `ITrayHost`, both are selected by a run-time platform
   check in the composition root, and both are marked unverified until someone runs them. What
   *is* verified from here is that neither stack throws when it is constructed on the wrong
   operating system — every one of their types is compiled into every build and is inert rather
   than absent off its own platform, and `ForeignPlatformStackTests` builds the same services in
   the same order the composition root does and asserts each reports itself unavailable. That
   catches the failure that would matter most, a constructor turning a degraded capability into
   a failure to start, and it catches nothing else.
2. **Provider formats are internal and disclaimed by their vendors.** Every field is optional at the
   parse boundary; a shape change degrades one metric to unavailable instead of breaking the app.
3. **The live Codex quota call requires the vendor CLI and the network**, so it is optional, rate
   limited, and falls back to local files.
4. **Native AOT for Avalonia is officially supported but rarely shipped.** It is a Release-only
   Windows setting first, behind a verification step, and can be turned off without code changes.
5. **Status line integration writes to a user configuration file.** It is opt-in behind a settings
   switch that is off on a fresh install and is never turned on by an upgrade or a first run. The
   installer merges rather than overwrites, keeps a timestamped backup, preserves the file's own
   comments and formatting, and reverts to the byte-for-byte original. An existing status line is
   **refused, not replaced**: the install reports `RefusedExistingStatusLine`, writes nothing, and
   the settings page says so and disables the switch. There is therefore nothing for revert to
   restore in that case, because nothing was ever taken away.
