# Working on Altim

Context for coding agents. `codex.md` points here; this file is the single copy.

Altim is a cross-platform desktop monitor for AI coding-agent usage. It sits in the tray, menu bar
or status area and answers one question: how much of your Claude and Codex allowance is left, and
when does it reset.

## The rules that decide arguments

These come from the product owner and outrank convenience, tidiness and your own judgement about
what would look better.

1. **Never fabricate usage information.** No interpolation, no carrying a figure forward, no
   plausible-looking placeholder.
2. **Never estimate a provider's limits and present them as authoritative.** If a vendor does not
   publish an allowance, Altim does not infer one.
3. **A metric that cannot be retrieved is shown as unavailable**, and the provider is architected so
   it can be added later. `PROVIDERS.md` grades every metric Documented / Best-effort / Unavailable
   and traces it to its source.
4. **Unknown and zero are different, everywhere.** "We have no data for this day" and "we have data
   and it says nothing was used" must never render the same. This has already been the deciding
   argument in several design calls and is the one rendering rule that may not be traded for visual
   tidiness.
5. **Local-first.** Prompts, source code, repository contents, conversation contents, commands,
   filenames and project names never leave the machine and are never persisted. Logs carry exception
   **types**, never messages or paths. `PRIVACY.md` is the contract; schema tests enforce parts of it.

## Stack

.NET 10, C#, Avalonia 12 with XAML, MVVM via CommunityToolkit.Mvvm, SQLite through
Microsoft.Data.Sqlite, xUnit v3 on Microsoft.Testing.Platform. Central package management in
`Directory.Packages.props`; never pin a version in a csproj.

**Ruled out by the owner, not up for revisiting:** Electron, Tauri, React, any WebView, Flutter, MAUI.

## Layout

| Project | Holds |
|---|---|
| `Altim.Core` | models, abstractions, usage maths, scheduling. No UI, no I/O. |
| `Altim.Storage` | SQLite: schema, the migration ladder, history, settings |
| `Altim.Providers`, `.Claude`, `.Codex` | reading each vendor's usage |
| `Altim.UI` | Avalonia controls, themes, views, view models |
| `Altim.Platform.Windows` / `.MacOS` / `.Linux` | tray, notifications, autostart, power events |
| `Altim.App` | composition root, runtime, maintenance pass |

Design docs are canonical. If code and `ARCHITECTURE.md`, `PROVIDERS.md`, `docs/DESIGN.md` or
`PRIVACY.md` disagree, the doc wins or the doc gets corrected in the same commit. Open work is
listed in `docs/BACKLOG.md`.

## Build and test

```
dotnet build Altim.sln -c Release      # 0 warnings; warnings are errors
dotnet test Altim.sln -c Release       # 3 skipped on Windows; more off it, by platform
```

- **A running Altim locks `Altim.App`'s output.** `dotnet build Altim.sln` then fails with MSB3027.
  Stop the app, or build a single project. `dotnet test Altim.sln` is unaffected: it does not build
  `Altim.App`.
- The portable (non-Windows) face is verified with `-p:AltimPortableBuild=true` plus
  `--artifacts-path`. Never `-p:BaseIntermediateOutputPath`: it applies to every project in the
  graph, they collide in one `obj`, and the build dies on a circular `ResolveProjectReferences`.
  See `packaging/README.md`.
- `--nologo` breaks Microsoft.Testing.Platform. Do not add it.

## Testing expectations

Tests here are expected to *discriminate*, not merely pass.

- **Mutation-check what you write.** Break the implementation deliberately and confirm the intended
  test fails. Every substantial change to this repository has been through that pass, and nearly
  every one of them caught a real defect — several times a defect in the test itself. A test that
  passes against a wrong implementation is worse than no test.
- Apply mutations **byte-exactly in Python with `assert old in source`**, and hash the file before
  and after to prove it changed. **Most sources are LF, but nine are CRLF** — three under
  `Altim.Platform.Windows/Interop`, `ProviderPageViewModel.cs`, `SettingsOptions.cs`,
  `PopupWindow.axaml`, and three under `tests/Altim.UI.Tests`. Read each file's own bytes and
  match its own endings. A replacement written with the wrong ending matches nothing, throws
  nothing, and reports a false green.
- A mutation that deletes the only call site of a private member will not compile, because
  warnings are errors (`IDE0051`). It proves nothing; respell it. **A harness that greps only for `error CS`
  reads that as zero failing tests and reports a false green** — two agents lost work to
  exactly this. Confirm the build succeeded before believing a run.
- Pixel tests (`tests/Altim.UI.Tests/PixelTests.cs`) guard design-system rules that no structural
  test can see. Dispose every captured bitmap.
- Source-shape tests guard invariants like "no view model holds a `Geometry`". They are load-bearing;
  read them before changing what they watch.
- Platform-specific tests use `WindowsFact` / `UnixFact`. xUnit v3 requires `[CallerFilePath]` and
  `[CallerLineNumber]` forwarded to the base attribute, or you get xUnit3003.
- **Measure the baseline yourself before you start.** The suite grows most days, so any count
  quoted in a brief is stale; report what you actually saw before and after.
- Do not assert on the state of asynchronous work at the moment you happen to look. "This task
  has not finished yet" is a bet on the thread pool being slow, and "the save has landed" is a
  bet that it was fast. Hold something the work provably needs, or await the signal the fake
  offers. Three tests here went red on CI and green everywhere else for exactly this.
- Budgets in tests are deadlock guards, not assertions. Make them generous: a tight one turns other
  people's CPU load into red builds here.

## Traps that have already cost time

- **The migration ladder's rung 1 *is* the create script.** A fresh database climbs every rung, so a
  table declared in both the create script and a later rung fails every first run with
  "table already exists". Give each migration its own DDL constant and never edit an earlier rung.
- **Do not rewrite a database row that has not changed.** Taking the writer stamps the clock the
  idle checkpoint reads, and the maintenance pass re-offers the same rows every few minutes, so an
  unconditional write means the write-ahead log is never truncated again. Compare with `IS NOT`,
  not `!=`: the columns are nullable and a value appearing where there was none is a change.
- **A provider's live token figure is not a daily amount.** One vendor reports a cumulative total
  over everything scanned; the other sums a bounded window of recent sessions and visibly oscillates
  within a minute. Daily token figures come only from per-day sources. Altim's own samples supply a
  day's peak percentage and never its tokens.
- **A `Geometry` cannot be built before Avalonia's rendering platform exists.** The failure lands in
  a type initialiser, which the CLR caches for the process lifetime and which takes ~30 tests down
  with it. Hold paths as strings, parse lazily.
- **`--` cannot appear inside an XML comment.** A double-hyphen CLI switch in a `.csproj` comment is
  a build error. Put the command in markdown and point at it.
- Windows console cannot encode many non-ASCII characters; stick to ASCII in tool output.

## Conventions

- **Commit messages never contain the word "Claude"**, even though it names a monitored provider.
  Say "the transcript provider", "each vendor's mark". Check `git log` for the house style: an
  imperative subject that says what changed and why, and a body that explains the reasoning rather
  than listing files.
- Commit per logical chunk and push often.
- The repository is public and MIT licensed. The vendor marks in
  `src/Altim.UI/Formatting/ProviderIdentity.cs` are the vendors' trademarks, used nominatively;
  `TRADEMARKS.md` is the notice and the MIT grant does not extend to them.
- No em dashes in product copy.

## When a plan or a brief is wrong

Say so and fix it. Instructions in this repository's plans have been wrong more than once in ways
that would have shipped real defects, and the most valuable thing an agent did was refuse the
instruction and explain why. Deviate, make it right, and report exactly what you changed.

Verify against reality before claiming something works. Every defect that reached this codebase
passed a green test suite first; the ones that were caught were caught by running the application
against real data.
