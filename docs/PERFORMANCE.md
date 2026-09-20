# Performance

Numbers, not adjectives. Everything below was measured on **Windows 11 26200, 16 cores, NVIDIA
discrete graphics, 1920x1080 at 100%**, against the real provider stores on that machine, with
an agent actively working throughout, which is the expensive case rather than the flattering
one. Those stores grow, so their size is stated beside the figures that depend on it.

| | Budget | Shipping build | `dotnet build` |
|---|---|---|---|
| Cold start to tray icon | < 800ms | **229ms** | 0.9–1.2s |
| Popup open, already warm | < 100ms | **8.4ms**, then under 1ms | 4–43ms |
| CPU, Altim **and its children** | < 5% of one core idle | see below | **3.1%** idle, 10.0% under load |
| **Idle private working set** | < 55MB | **42MB** | 48MB |
| Idle working set | < 110MB | **98MB** | 147MB |
| Database | < 5MB/year | **270KB** after 7.4 hours; see below | same file |

The memory rows and the cold-start figure were measured on **2026-09-18**, 150 seconds after
launch with no window ever opened: six interleaved runs of the shipping build, one of the
framework-dependent build. The store they ran against was 3,864 Claude Code transcripts
totalling 1.38GB and 2,518 Codex rollout files. The other rows are from the earlier runs
described below.

"Shipping build" is `dotnet publish -c Release -r win-x64`, which compiles ahead of time.
`dotnet build` produces a framework-dependent build that loads 77 managed assemblies and JIT
compiles them; it is what you get from `dotnet run`, and it is three to four times slower to the
tray icon. Both are honest numbers for what they are — and the gap between them is a good
illustration of why the budget is on the private figure. The framework-dependent build's working
set is **49MB** higher, because it maps a runtime and 77 assemblies; its private working set is
**6MB** higher, because mapped code is not memory Altim is spending.

**The memory budget is now stated in private working set, and the number went down rather than
up.** It said 80MB, which nothing ever met, and then 120MB, which the shipping build was missing
by a few megabytes. Both were working set, and working set is the wrong thing to budget: 55MB of
Altim's was pages shared with the rest of the desktop — Windows' own DLLs, the ICU data file, the
font cache, the graphics driver — which are resident on the machine whether or not Altim is
running. What Altim is answerable for is the private part, so that is what the budget is: **under
55MB of private working set, against 42MB measured**. The total is published beside it because it
is the figure one command returns.

**Chasing the old number found a real bug.** The transcript reader rented one buffer for the
whole slice it was about to read, which for the handful of transcripts over 85,000 bytes meant a
large-object allocation that the shared array pool then held until a gen2 collection that an idle
tray process never runs. It was **26.8MB of large object heap** against 0.09MB with an empty
store, while the live object graph was 4.2MB. The reader now slides a 64KB window, and the same
binary measures **97.6MB working set against 116.2MB**, with the run-to-run spread down from
13MB to 3MB. [ARCHITECTURE.md](../ARCHITECTURE.md#the-idle-memory-number-which-one-it-is-and-where-it-goes)
has the full breakdown: what the empty-Avalonia floor costs, what each provider store costs, what
the GC settings do and do not do, and the one change that would take another 29MB off.

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
- **Working set** comes from the process itself: `(Get-Process Altim).WorkingSet64`. It moves by
  a few megabytes between one look and the next, so take several.
- **Private working set**, which is what the budget is stated in, is the resident pages that are
  not shareable with anything else. The one-line version is the performance counter
  `(Get-Counter '\Process(Altim)\Working Set - Private')`; the exact version is to walk the
  process with `VirtualQueryEx` and ask `QueryWorkingSetEx` which pages are resident and which of
  those have the `Shared` bit clear. The two differ by about 2MB, because the counter excludes
  copy-on-write pages in mapped images and the walk includes them. The figures published here are
  from the walk.
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
