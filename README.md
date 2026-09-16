<div align="center">
  <img src="assets/brand/altim-logo-source.png" alt="Altim" width="160">

  **AI usage, at a glance.**

  [altim.dev](https://altim.dev)
</div>

---

Altim is a lightweight, local-first desktop monitor for AI coding agent usage. It sits in
your system tray (Windows), menu bar (macOS) or status area (Linux) and answers one
question: **what are my AI coding tools consuming right now?**

> **Status: early development.** The architecture and provider data sources are being
> established first — see [ARCHITECTURE.md](ARCHITECTURE.md) and [PROVIDERS.md](PROVIDERS.md).

## What it shows

- Session and weekly usage against each provider's limits
- Time until the next reset
- Token counts where a provider exposes them
- Whether an agent is working right now, and when it was last active
- Local usage history over 24 hours, 7 days and 30 days

## Providers

| Provider | Status |
|---|---|
| Claude / Claude Code | in development |
| OpenAI Codex | in development |
| Gemini CLI, Cursor, GitHub Copilot | planned |

Altim never invents numbers. Every metric is traced to a documented local source in
[PROVIDERS.md](PROVIDERS.md), and anything that cannot be read reliably is shown as
unavailable rather than estimated.

## Platforms

Windows, macOS and Linux, from one Avalonia UI codebase with native platform services
for tray/menu-bar behaviour, notifications, autostart and power events.

## Privacy

Local-first by default. Altim reads usage metadata only, stores it in a local SQLite
database, and never transmits prompts, source code, repository contents, conversation
contents, commands or filenames anywhere. See [PRIVACY.md](PRIVACY.md).

## Performance

Numbers, not adjectives. Everything below was measured on **Windows 11 26200, 16 cores, NVIDIA
discrete graphics, 1920x1080 at 100%**, against real provider stores on that machine — 28.3 GB
of Codex rollouts across 2,518 files and 1.3 GB of Claude Code transcripts across 2,069 — with
an agent actively working throughout, which is the expensive case rather than the flattering
one.

| | Budget | Shipping build | `dotnet build` |
|---|---|---|---|
| Cold start to tray icon | < 800ms | **260ms** | 0.9–1.2s |
| Popup open, already warm | < 100ms | **8.4ms**, then under 1ms | 4–43ms |
| Idle CPU | < 2% of one core | **1.4%** idle, 2.4% under load | same within noise |
| Idle working set | < 120MB | **107MB** | 156MB |
| Database | < 5MB/year | **270KB** after 7.4 hours; see below | same file |

"Shipping build" is `dotnet publish -c Release -r win-x64`, which compiles ahead of time.
`dotnet build` produces a framework-dependent build that loads 77 managed assemblies and JIT
compiles them; it is what you get from `dotnet run`, and it is 49MB heavier and three to four
times slower to the tray icon. Both are honest numbers for what they are.

**The working-set budget used to say 80MB and no build has ever met it.** It is now 120MB,
against a measured 107MB. Most of that is mapped framework, Avalonia, Skia and graphics-driver
pages; the live managed heap is 7MB. The hidden popup window costs 2.7MB of it, the provider
caches almost nothing, and the GC configuration nothing at all — all three were measured before
the number was changed. [ARCHITECTURE.md](ARCHITECTURE.md#the-working-set-budget-was-wrong-and-this-is-where-the-memory-goes)
has the full breakdown, including the one change that would reach 80MB and why it has not been
made.

**Idle CPU** is a share of *one* core, not of the machine, because "0.1% of the machine" means
different things on a four-core laptop and a sixteen-core desktop and is not a property of the
program. 1.4% of one core is what the process costs with both provider stores empty and nothing
happening at all — it is Avalonia's floor for holding a live hidden window, not Altim's work.
Under continuous agent activity, with the filesystem watchers firing, it reaches about 2.4%.

**Database growth** has two figures and they mean different things. The file held **270KB for
2,373 samples** after 7.4 hours of continuous agent activity — about 114 bytes per sample
including its index. Rows are written only when a value changes, so that rate is a ceiling
rather than a cadence. Past 30 days they are down-sampled to one row per metric per hour, which
is what bounds the long run: five metrics at 24 rows a day is **about 4.4MB a year**, on top of
a rolling window of at most a month of raw samples. The write-ahead log in front of it is
capped at roughly 1MB while Altim is working and emptied once two minutes pass with no write —
SQLite's own default would leave it sitting at 3.9MB for ever.

### How to measure it yourself

- **Start-up, popup open and first readings** are timed by the application and written to
  `%APPDATA%\Altim\altim.log`. Launching Altim a second time signals the running instance to
  surface its panel, which is a repeatable way to time an open without touching the mouse.
- **Working set and CPU** come from the process itself: `(Get-Process Altim).WorkingSet64`, and
  `TotalProcessorTime` sampled three minutes apart divided by the elapsed wall time for the
  share of one core. Measure a minute after start at the earliest — the first pass over a
  provider store is the expensive one, and it is deliberately not on the start-up path. For the
  idle floor rather than the working figure, point `CODEX_HOME` and `CLAUDE_CONFIG_DIR` at empty
  directories, which leaves the process with nothing to react to.
- **Where the memory is** needs the committed regions rather than the totals: walk the process
  with `VirtualQueryEx` and bucket by `MEM_IMAGE` / `MEM_MAPPED` / `MEM_PRIVATE`, then ask
  `QueryWorkingSetEx` which of those pages are actually resident. The managed share of it is
  `dotnet-counters --counters System.Runtime`, and a `dotnet-gcdump` report gives live objects
  by type.
- **Database and log** are the file sizes in `%APPDATA%\Altim`.

## Building

Requires the **.NET 10 SDK**. Nothing else — the UI, database and platform integrations come
from NuGet packages pinned in `Directory.Packages.props`.

```bash
dotnet build Altim.sln -c Release      # warnings are errors
dotnet test Altim.sln -c Release
dotnet run --project src/Altim.App
```

## Repository layout

| Path | What lives there |
|---|---|
| `src/Altim.Core` | models, interfaces, usage math, reset arithmetic, the monitoring scheduler, notification thresholds. Depends on nothing but the BCL |
| `src/Altim.Providers` | shared provider plumbing: tail readers, incremental scanning, process detection, CLI invocation |
| `src/Altim.Providers.Claude`, `.Codex` | one project per provider |
| `src/Altim.Storage` | SQLite: migrations, usage history, retention, settings |
| `src/Altim.UI` | Avalonia views, view models, design system, custom controls |
| `src/Altim.Platform.Windows`, `.MacOS`, `.Linux` | native tray, notifications, autostart, power events |
| `src/Altim.App` | composition root |
| `docs/DESIGN.md` | the design system every view implements |

Dependencies run one way: `App → UI → Core`, `App → Platform.* → Core`,
`App → Providers.* → Providers → Core`, `Storage → Core`. Nothing depends on `App`, which is what
keeps the logic testable without a UI.

## Adding a provider

1. Create `src/Altim.Providers.<Name>` and implement `IUsageProvider`.
2. Report only what the provider genuinely exposes. A metric you cannot read is `null`, never `0`
   and never an estimate. Classify limit windows by their length, not by the order a provider
   happens to return them in.
3. Document every field's source in [PROVIDERS.md](PROVIDERS.md), graded documented, best-effort or
   unavailable, and only parse numeric and timestamp fields out of provider files.
4. Register it in the composition root.

No UI work is required: views render whatever metrics a provider reports.

## Platform behaviour

| | Windows | macOS | Linux |
|---|---|---|---|
| Presence | system tray, own `Shell_NotifyIcon` host | menu bar, `NSStatusItem` | StatusNotifierItem |
| Panel anchor | exact icon rectangle, else cursor, else screen corner | status item frame | screen corner (the protocol carries no geometry) |
| Notifications | Windows App SDK | `UNUserNotificationCenter` | `org.freedesktop.Notifications` |
| Start at login | `Run` key, state read from `StartupApproved` | `SMAppService` | XDG autostart |

Where a desktop cannot do something, Altim degrades visibly rather than pretending.

## Packaging

| Platform | Artefacts | State |
|---|---|---|
| Windows | `Setup.exe`, portable zip, delta updates | built, installed, run and uninstalled on Windows 11 |
| Linux | AppImage, `.deb`, `.rpm` | packages built and their metadata checked; **never installed or run** |
| macOS | universal `.app`, DMG | **never executed** — no macOS host |

Scripts live in [`packaging/`](packaging/), with a README covering how to produce each artefact by
hand and what a maintainer needs in order to sign them. A version tag builds all three through
`.github/workflows/release.yml` and attaches them to a draft release.

Nothing is signed yet. Windows installers are unsigned until a certificate exists, and the macOS
signing and notarisation steps skip cleanly when the Apple secrets are absent, so the workflow runs
end to end without them and produces an unsigned bundle.

The `.deb` dependency list is **hand-maintained on purpose**: only one of the nine X11 and
fontconfig libraries Avalonia needs is visible to automatic dependency detection, because the rest
are loaded by name at runtime. A generated list under-declares, and the package then installs
cleanly and fails to start.

## License

MIT — see [LICENSE](LICENSE).

## Support

[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-FFDD00?logo=buymeacoffee&logoColor=000)](https://buymeacoffee.com/mpge)
