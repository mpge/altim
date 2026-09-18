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

The same database also keeps one row per provider per day, which is what the usage map draws. That
row holds a provider identifier, the date, four token counts, a peak percentage and one word saying
whether the figure was observed or backfilled. There is nowhere in it for a project, a path, a
prompt, a command or anything said in a conversation, and the readers that fill it carry none of
those out of a provider's files in the first place.

Daily totals are kept indefinitely; the samples behind them are not. Samples older than 30 days are
collapsed to one row per metric per hour and the originals are deleted, so what survives in the long
run is the coarser record. Reducing a day of samples to one total is strictly less revealing than
the samples themselves, which Altim was already storing.

Altim does not store project names, prompts, commands or file paths in that database. Agent
activity is shown live in the interface and is not written to history.

One provider's store is worth naming specifically. Gemini CLI keeps its session transcripts in the
same directory tree as its Google sign-in credentials, and each transcript's first line holds the
absolute paths of the folders that session was working in. Altim's reader never lists that
directory: it starts three levels below it, at the `chats` folder the transcripts are actually in,
and it reads token counts and timestamps out of them and nothing else. Your credential files are
never opened, and neither the folder paths nor the project name reaches Altim's database, its logs
or its screen. A test holds every one of those files open and unreadable and checks that Altim still
works, which it only can if it never tried to read them.

You can delete everything from **Settings → Privacy → Clear usage history**, or by deleting the
database file. Settings live alongside it.

## What Altim writes outside its own database

One feature, and only with your say-so.

Claude Code publishes reset times, the spend limit and its window percentages to one place: a
status-line command you configure it to run. Altim can register itself as that command, which means
writing one `statusLine` entry into your Claude Code `settings.json`.

- **It is off until you turn it on**, in Settings → Providers. Nothing is written on a first run or
  an upgrade.
- Your settings file is **copied to a timestamped backup first**, and the edit is a merge: your
  comments, indentation, line endings, key order and any setting Altim has never heard of are all
  left exactly as they were. Turning the switch off restores the original.
- If you already have a status line of your own, Altim **refuses to install** rather than replacing
  it, and says so.

While it is on, Claude Code runs `Altim.exe altim-statusline` whenever it redraws its status line
and hands that command its status-line payload. The payload contains your working directory, your
project directory, the transcript path, a session id and the model. Altim writes **none** of them.
What it writes is one small file of numbers, `altim-statusline.json`, beside your Claude Code
settings: window percentages, reset timestamps, session cost, context-window and prompt-cache token
counts. The printed status line is built from the same numbers. Nothing leaves the machine, and the
file is Altim's own to delete.

## Network activity

Altim itself makes no network requests for usage data.

The Gemini CLI integration makes none at all, of any kind: it reads files and looks for a running
process, and that is the whole of it.

One provider integration is an exception worth stating plainly: live Codex quota comes from asking
the installed Codex CLI, over local inter-process communication, and that CLI then contacts OpenAI
using credentials it already holds. Altim sees only the resulting numbers. This is the same call
the CLI makes for its own status display. Altim polls it no more than once a minute, and you can
turn it off in Settings, in which case Altim falls back to reading local session files only.

## Telemetry

None. Altim has no analytics, no crash reporting service and no update ping beyond what you
explicitly trigger.
