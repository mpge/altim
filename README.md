<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/altim-logo-white.png">
    <img src="assets/brand/altim-logo-ink.png" alt="Altim" width="200">
  </picture>

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
- A year of daily usage as a calendar map, one square per day per provider

## The usage map

The History page opens on **Daily usage**: a year of squares, one per local calendar day, a row per
provider, and a combined row summing their tokens when there is more than one. Hover a square or
focus it from the keyboard and it gives the date, the four token components the day was reported in,
the highest percentage of an allowance reached that day, and whether the figure was observed or
backfilled. The combined row lists each provider's peak on its own line and never averages them: two
providers' percentages are measured against different limits, and a number made by adding them would
be a number nobody reported.

A square's shade is a rank rather than an amount. Days that have a figure are sorted and split into
five bands against your own history, so the darkest square means one of your own heaviest days and
not any particular number of tokens. A linear ramp would draw every ordinary day as the palest
square and one cache-heavy day as black.

**Token volume is not a measure of productivity, and the map must not be read as one.** A day spent
re-reading a large codebase moves tens of millions of cache tokens for a handful of edits; a day of
careful work on a hard problem can move a hundredth of that and be worth far more. The map will draw
the first as a dark square and the second as a pale one, because it is a picture of volume and
volume is all it is. The tooltip's split into input, output, cache read and cache write is there so
a heavy day can be seen for what it actually was.

**An unknown day and a day that used nothing are drawn differently.** A hairline outline with no
fill means there is no data for that day: before Altim was installed, further back than the
provider's own history reaches, or a provider that was not installed at the time. The faintest fill
in the ramp means there is data and it says nothing was used. Neither is allowed to stand in for the
other.

**The squares carry the providers' own daily figures.** A live reading is a running total rather
than a day's spending, so Altim asks each provider for its own history instead, at most once a day.
What Altim's own samples contribute is the day's peak percentage, which is a real measurement of how
close to a limit you came. The two are merged field by field, so a day can hold a backfilled token
figure beside a peak observed while Altim was watching, or a token figure and no peak at all because
Altim was not running that day.

How far back a row reaches is whatever a provider still holds, and the two do not match. On the
machine these figures were measured, Codex accounted for 100 days spanning about nine months while
the Claude Code transcript store had four days left on it. Everything older is unknown and is drawn
as unknown. Codex also reports one undifferentiated figure per day rather than a breakdown, so its
days carry that figure as input with the other three components unreported: the tooltip shows a
split only where a provider gave one.

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
| CPU, Altim **and its children** | < 5% of one core idle | see below | **3.1%** idle, 10.0% under load |
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

**CPU is a share of *one* core, not of the machine**, because "0.1% of the machine" means
different things on a four-core laptop and a sixteen-core desktop and is not a property of the
program. It sits in the `dotnet build` column because that is the build it was measured on, and
the shipping column is left empty rather than filled with a number from a different binary. What
dominates it is the provider command lines, which are the same processes whichever way Altim
itself was compiled; Altim's own share is the part ahead-of-time compilation would move, and that
is the smaller half of a figure this size.

**It now counts the processes Altim starts, and it used to count only Altim.** That is not a
rounding error. Reading Claude Code's session list means running `claude agents --json`, and on
this machine one of those starts **105 processes and spends 5.6 seconds of CPU** before it
answers — it takes 18 to 27 seconds of wall clock, longer than the 15 seconds Altim allows it, so
it is usually killed part way through and the reading falls back to a process scan. It ran on
every refresh, and refreshes are driven by filesystem events, so a session writing transcripts
continuously cost **259 child processes a minute and 23.2% of one core** while the published
figure — taken from `(Get-Process Altim).TotalProcessorTime` — said 2.4%. That figure was not
wrong about what it measured. It measured the wrong thing.

Two changes, then an honest number. The session listing now runs at the polling floor rather than
on every refresh, and backs off to five minutes when nothing that looks like an agent is running
or when the previous attempt did not answer. Neither costs a status: whether an agent is running
comes from a process scan that reads an executable name and a process id. Measured back to back on
the same machine under the same load, that takes the whole tree from **23.2% to 10.0% of one core,
and from 259 child processes a minute to 61**.

**The budget has moved with it, because 10% does not fit under 2%.** The old number was written
for a process measured alone, and keeping it while counting the children would have meant
publishing a budget the product misses by five times. It is now **under 5% of one core at idle**,
against a measured 3.1%, with the load figure stated separately rather than folded in and
rounded down. Idle is the one number the gating did not move — at idle the polling floor already
held the listing to once a minute, and what is left there is the provider CLIs answering once a
minute each, which is the price of reading anything at all. The way to make that smaller is to run
them less often, not to measure them less honestly.

Altim's own share, which is what the old figure reported, is **2.7% of one core under load and
1.8% at idle**. That part is Avalonia's floor for holding a live hidden window plus the file
reads, and these changes left it where it was.

**Database growth** has two figures and they mean different things. The file held **270KB for
2,373 samples** after 7.4 hours of continuous agent activity — about 114 bytes per sample
including its index. Rows are written only when a value changes, so that rate is a ceiling
rather than a cadence. Past 30 days they are down-sampled to one row per metric per hour, which
is what bounds the long run: five metrics at 24 rows a day is **about 4.4MB a year**, on top of
a rolling window of at most a month of raw samples. The write-ahead log in front of it is
capped at roughly 1MB while Altim is working and emptied once two minutes pass with no write —
SQLite's own default would leave it sitting at 3.9MB for ever.

**That last part was not true, and finding out is what this change is.** The clock the
checkpoint reads was stamped when the writer was *taken*, and Altim takes the writer constantly to
find out there is nothing to do — the history service, the notification state, the down-sampler and
the vacuum check. The maintenance pass runs two of those in the same method that then asks whether
two minutes have passed without a write, so the answer was always no and the checkpoint was
unreachable by construction. Measured over nine minutes with both provider stores empty and nothing
whatever to report, it never ran once, and the log sat at 869KB in front of a 397KB database.

The clock now means what it says: a lease stamps it only when it actually changed a row. Two of the
callers were also asking for the writer when they had nothing to write — the notification state was
rewritten on every reading whether or not it had changed, and the history took the writer to
discover that a reading had not moved — and both now compare before the writer is asked for.

### How to measure it yourself

- **Start-up, popup open and first readings** are timed by the application and written to
  `%APPDATA%\Altim\altim.log`. Launching Altim a second time signals the running instance to
  surface its panel, which is a repeatable way to time an open without touching the mouse.
- **Working set** comes from the process itself: `(Get-Process Altim).WorkingSet64`.
- **CPU has to count the children, or it counts almost nothing.** A provider CLI runs for a
  second or two and exits, so it is gone before a sampling loop can find it, and
  `TotalProcessorTime` on the Altim process never knew it existed. Put Altim in a **job
  object** — `CreateJobObject`, `AssignProcessToJobObject` — and read
  `JobObjectBasicAccountingInformation`: `TotalUserTime` plus `TotalKernelTime` are the summed
  CPU of every process that has ever been in the job, including the ones that have already
  exited, and `TotalProcesses` counts how many there have been. Divide the CPU by the elapsed
  wall time for the share of one core, and read the Altim process's own `TotalProcessorTime`
  alongside it: the difference between the two is what the old figure was missing. Measure a
  minute after start at the earliest — the first pass over a
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

## Trademarks

Altim is an independent open-source project, not affiliated with or endorsed by Anthropic or
OpenAI. Claude and Claude Code are trademarks of Anthropic; OpenAI and Codex are trademarks of
OpenAI. Their marks appear here only to identify which of your own tools a figure belongs to.

The MIT licence covers Altim's code and grants no rights in anyone's trademarks. See
[TRADEMARKS](TRADEMARKS.md) before reusing the provider marks in a fork.

## Support

[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-FFDD00?logo=buymeacoffee&logoColor=000)](https://buymeacoffee.com/mpge)
