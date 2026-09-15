# Privacy

Altim is a local-first utility. It reads usage metadata from AI coding tools already installed on
your machine, keeps it in a local database, and shows it to you. That is the whole product.

## What Altim never does

- It never transmits prompts, source code, repository contents, conversation contents, commands,
  filenames or project paths anywhere.
- It never sends your usage data to Altim servers. There are none.
- It never opens credential files. Provider authentication stays with the provider's own CLI.
- It never includes file contents or conversation text in logs, diagnostics or crash reports.

## What Altim reads

Provider session files contain a great deal of sensitive material: your prompts, the model's
reasoning, file contents, command output, patches, repository URLs and working directories.

Altim's readers parse only numeric and timing fields out of those files:

- token counts
- usage percentages and window lengths
- reset timestamps
- plan and limit identifiers
- event timestamps

Everything else on a line is discarded before it leaves the reader. Raw lines are never persisted.

For the exact files, fields and commands used per provider, see [PROVIDERS.md](PROVIDERS.md).

## What Altim stores

A single SQLite database in your user profile, containing usage snapshots over time: timestamps,
percentages, token counts, limit windows and reset times. That is what powers the 24-hour, 7-day
and 30-day history.

Altim does not store project names, prompts, commands or file paths in that database. Agent
activity is shown live in the interface and is not written to history.

You can delete everything from **Settings → Privacy → Clear usage history**, or by deleting the
database file. Settings live alongside it.

## Network activity

Altim itself makes no network requests for usage data.

One provider integration is an exception worth stating plainly: live Codex quota comes from asking
the installed Codex CLI, over local inter-process communication, and that CLI then contacts OpenAI
using credentials it already holds. Altim sees only the resulting numbers. This is the same call
the CLI makes for its own status display. Altim polls it no more than once a minute, and you can
turn it off in Settings, in which case Altim falls back to reading local session files only.

## Telemetry

None. Altim has no analytics, no crash reporting service and no update ping beyond what you
explicitly trigger.
