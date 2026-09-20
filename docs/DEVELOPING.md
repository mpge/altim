# Developing Altim

## Build and test

Requires the **.NET 10 SDK**. Nothing else — the UI, database and platform integrations come
from NuGet packages pinned in `Directory.Packages.props`.

```bash
dotnet build Altim.sln -c Release      # warnings are errors
dotnet test Altim.sln -c Release
dotnet run --project src/Altim.App
```

Continuous integration runs both of the first two on **Windows, Linux and macOS**, so every change
is compiled with warnings as errors and tested on all three; the macOS runner is Apple silicon, so
it is also the only place the suite runs on arm64. Each platform skips a different handful of
tests, because each of those skips is a test of behaviour that exists on one operating system only.
`.github/workflows/build.yml` lists the counts and the rule behind them.

Compiling and passing tests on macOS is not the same claim as running on a Mac, and only the first
one is made. See [Packaging](INSTALL.md#packaging).

## Repository layout

| Path | What lives there |
|---|---|
| `src/Altim.Core` | models, interfaces, usage math, reset arithmetic, the monitoring scheduler, notification thresholds. Depends on nothing but the BCL |
| `src/Altim.Providers` | shared provider plumbing: tail readers, incremental scanning, process detection, CLI invocation |
| `src/Altim.Providers.Claude`, `.Codex`, `.Gemini` | one project per provider |
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
3. Document every field's source in [PROVIDERS.md](../PROVIDERS.md), graded documented, best-effort or
   unavailable, and only parse numeric and timestamp fields out of provider files.
4. Register it in the composition root.
5. Implement `IUsageHistorySource` only if the provider genuinely reports whole days. A provider
   that cannot reach into the past leaves those days unknown, which the map draws differently from
   a day that used nothing.

No UI work is required: views render whatever metrics a provider reports.
