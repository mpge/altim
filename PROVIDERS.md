# Providers: where every number comes from

Altim never invents a metric. Every value on screen is traced to a source in this document.
If a source does not report something, the UI shows **"Not reported by this provider"** — not a
zero, not an estimate.

Each source is graded:

- **Documented** — vendor-documented interface, stable to depend on.
- **Best-effort** — real and verified, but undocumented or experimental; may break on a vendor update.
- **Unavailable** — cannot be obtained locally today. Modelled in the provider contract so it can
  light up later without UI changes.

Verification date: **2026-09-15** for Codex and Claude Code, **2026-09-18** for Gemini CLI.
Codex and Claude Code findings were executed against live installations on Windows 11 unless marked
unverified. **Gemini CLI is not installed on the verification machine**, so its section was
established by reading the vendor's own open source at a named tag and its published documentation,
and says so at the top rather than borrowing the others' standing.

---

## OpenAI Codex

Verified against `codex-cli 0.153.4` (npm `@openai/codex`), ChatGPT authentication, 2,518 local
sessions.

### Sources

| Metric | Source | Grade |
|---|---|---|
| Live usage %, window length, reset time, per limit family | `codex app-server` JSON-RPC → `account/rateLimits/read` | Best-effort |
| Plan type, credit balance, reset credits | same response: `planType`, `credits`, `rateLimitResetCredits` | Best-effort |
| Daily token history (one bucket per day that had usage), lifetime tokens, streaks | `codex app-server` JSON-RPC → `account/usage/read` | Best-effort |
| Day backfill when the once-a-minute gate is already spent | the daily buckets of the last successful `account/usage/read`, at most 5 minutes old | Best-effort |
| Historical usage snapshots (offline fallback) | `<CODEX_HOME>/sessions/**/rollout-*.jsonl`, lines where `type == "event_msg"` and `payload.type == "token_count"` → `payload.rate_limits` | Best-effort |
| Per-turn and per-session tokens | same lines → `payload.info.last_token_usage` (delta), `payload.info.total_token_usage` (cumulative) | Best-effort |
| Per-turn tokens, newer schema | rollout lines where `type == "token_usage_record"` → `payload.usage`, `turn_token_usage`, `thread_token_usage` | Best-effort |
| Model context window | `payload.info.model_context_window` | Best-effort |
| Recent threads, rollout paths, last activity | `<CODEX_HOME>/state_5.sqlite` → `threads(tokens_used, model, rollout_path, updated_at_ms, …)`, opened read-only | Best-effort |
| Canonical paths, auth mode, rollout counts | `codex doctor --json` (`schemaVersion: 1`, redacted by design) | Best-effort |
| Tokens for one non-interactive run | `codex exec --json` → `turn.completed.usage` | Documented |
| Running session detection | process named `codex` / `codex.exe`, subcommand in argv | Documented |
| Published numeric 5-hour / weekly allowance | — OpenAI publishes only estimated message *ranges* per plan | Unavailable |
| Cost in currency | — Codex records tokens only | Unavailable |

`codex exec --json` emits **no** rate-limit data (verified: zero matches for `rate|limit|percent`
in its output). There is no `codex usage` or `codex status` subcommand; `/status` is interactive
only. No public OpenAI API exposes an individual ChatGPT-plan account's Codex usage: the platform
Usage API is organisation-and-API-key scoped, and Codex Enterprise Analytics is workspace scoped.

### Live quota: app-server JSON-RPC

Spawn `codex app-server` and speak line-delimited JSON-RPC over stdio:

```jsonc
{"id":1,"method":"initialize","jsonrpc":"2.0","params":{"clientInfo":{"name":"altim","version":"…"}}}
{"method":"initialized","jsonrpc":"2.0","params":null}
{"id":2,"method":"account/rateLimits/read","jsonrpc":"2.0"}
{"id":3,"method":"account/usage/read","jsonrpc":"2.0"}
```

Response carries `rateLimits` (one family) **and** `rateLimitsByLimitId` (every family). Altim reads
`rateLimitsByLimitId`, because the top-level object silently picks one family.

**This is local IPC but not offline.** The CLI calls OpenAI's backend with the user's stored
ChatGPT token, and hard-errors under API-key auth. Consequences for Altim:

- Poll no faster than every 60 seconds, and prefer the `account/rateLimits/updated` push.
- Skip live polling entirely when `codex doctor --json` reports a non-ChatGPT auth mode.
- Altim never touches `auth.json` and never calls OpenAI endpoints itself. The CLI owns auth.

### Window semantics — the fragile part

Limits are reported as `primary` and `secondary` slots, and **slot position does not mean what you
would assume**. Verified drift on one account:

```
2025-12 → limit "codex":           primary 300 min (5h),  secondary 10080 min (weekly)
2026-09 → limit "codex":           primary 10080 min,     secondary null
2026-09 → limit "codex_bengalfox": primary 300 min,       secondary 10080 min
```

Rules Altim follows:

1. Classify a window by `window_minutes`, never by slot name: ≈300 → 5-hour, ≈1440 → daily,
   ≈10080 → weekly, ≈43200 → monthly. Match tolerantly — values of 299 and 10079 occur.
2. Render one meter per limit family present, labelled from the window length and `limit_name`.
   Families observed: `codex`, `codex_bengalfox` (GPT-5.3-Codex-Spark), `premium`.
3. If a family stops reporting a window, that meter disappears rather than showing a stale number.

### Field naming

The same concept is spelled three ways. Altim normalises at the boundary:

| Concept | Rollout JSONL | app-server |
|---|---|---|
| percent used | `used_percent` (float) | `usedPercent` (int) |
| window length | `window_minutes` | `windowDurationMins` |
| reset time | `resets_at` (unix seconds) | `resetsAt` |

`resets_in_seconds` is legacy — zero occurrences across 2,517 local files. Ignore it.

### Reading history without melting the disk

The local session store here is **28.3 GB across 2,518 files**. Altim never full-scans:

- The last `token_count` record sits within ~1.2 KB of end-of-file, so Altim reads the final 8 KB.
- Candidate files come from `state_5.sqlite` `threads` ordered by `updated_at_ms`, not a directory walk.
- `sessions/` and `archived_sessions/` are de-duplicated.
- `total_token_usage` is cumulative per session and `last_token_usage` is the delta — summing the
  former double-counts badly.
- Rollouts may be `.zst` compressed (unverified in practice); unreadable files are skipped, not guessed at.

**The live token figure is not a per-day amount, and cannot be made into one.** It is a sum over a
bounded window of the most recently active sessions, each contributing that session's own cumulative
total. Which sessions count as "most recent" changes between one read and the next, so the number
moves in both directions for reasons that have nothing to do with usage: against the live store it
alternated between 255,886,883 and 184,586,597 **inside the same minute**. Nothing derived from a
series of these readings — their sum, their last, or their highest — is what a day cost. The map's
daily figures therefore come from `account/usage/read`'s daily buckets and from nowhere else;
Altim's own samples contribute that day's peak percentage only. A bucket is one undifferentiated
figure with no breakdown, so a backfilled Codex day fills the **input** component and leaves output,
cache read and cache write unreported: the map's tooltip breaks a day down only where the provider
broke it down.

**The backfill reaches as far back as the reply's own buckets, and answers from the last reading
when the gate is shut.** The buckets are the days that had usage, not a window, so the count is not
the span and neither of them is fixed: read on 2026-09-17 the reply carried **100 buckets, the
oldest dated 2025-12-22**, which is 100 used days spread across about nine months. A quieter account
answers with fewer buckets over a different stretch, so nothing here depends on the number. It makes
the same gated `account/usage/read` call as the live meter, and on a real machine it almost
never gets to: `monitoring.refresh_seconds` is 60 and the gate opens once a minute, so the
scheduler's live read takes it within seconds of it opening, while the backfill asks from the
five-minute housekeeping pass at an arbitrary instant. Measured on the verification machine,
`maintenance.last_backfill.claude` was stamped and `maintenance.last_backfill.codex` did not exist
at all after repeated passes — an empty answer is not recorded as a run, so it was retried for ever
and the Codex row of the map would have stayed unknown permanently. A backfill that finds the gate
shut therefore answers from the daily buckets the last successful call returned, bounded to
`LiveSnapshotRetention` (5 minutes), which is the same age policy the live meter already applies to
a remembered quota snapshot. It makes no extra call and starts no process.

The grade is unchanged at **best-effort**: these are the provider's own figures, dated by the day
the provider put on them and not by when Altim read them, and whole-day totals do not move
meaningfully over five minutes. Nothing is interpolated or carried forward — a day the reply did
not account for is absent from a remembered reading exactly as it is from a fresh one, and stays
unknown rather than becoming a zero. Past the bound, before any call has succeeded, and whenever
network permission is off (which forgets the vendor's figures outright), the backfill answers with
nothing and the days stay unknown.

### Paths

| Item | Windows | macOS / Linux |
|---|---|---|
| Home | `%USERPROFILE%\.codex` | `~/.codex` |
| Override | `CODEX_HOME` (must exist if set; no XDG fallback) | same |
| Sessions | `<home>\sessions\YYYY\MM\DD\rollout-<ISO>-<uuid>.jsonl` | same |
| Archived | `<home>/archived_sessions/` | same |
| State DB | `<home>\state_5.sqlite` (filename is schema-versioned — discover via `codex doctor --json`) | same |

macOS and Linux paths come from upstream source, not local execution: **unverified**.

### Known fragility

- `app-server` is marked experimental, requires network and a valid ChatGPT token.
- SQLite filenames carry schema versions (`state_5`, `logs_2`, `thread_history_1`) and have changed before.
- A storage migration to paginated thread history is in flight; JSONL is still written today.
- Local token sums do not match server accounting (measured ~16% apart), so Altim labels local
  aggregates as locally observed and shows server figures when available.
- `logs_2.sqlite` looks tempting and is not a usage source (604 MB, stale by days).

---

## Claude Code (Anthropic)

Verified against version `2.1.273` (native install) on Windows 11 with subscription authentication,
against 2,069 local transcript files.

### Sources

| Metric | Source | Grade |
|---|---|---|
| 5-hour and weekly usage %, **with reset timestamps** | status line command stdin JSON → `rate_limits.{five_hour,seven_day,spend_limit}.{used_percentage,resets_at}` | Documented |
| Live session cost (USD) | status line JSON → `cost.total_cost_usd` | Documented |
| Context window state | status line JSON → `context_window.*` | Documented |
| Prompt cache health | status line JSON → `prompt_cache.*` | Documented |
| Usage summary without an open session | `claude -p --output-format json "/usage"` → human-readable text in `.result` | Best-effort |
| Per-request tokens, per model, historical | `<config>/projects/**/*.jsonl`, `assistant` lines → `message.usage.*`, `message.model` | Best-effort |
| Subagent tokens | `<config>/projects/<slug>/<session>/subagents/agent-*.jsonl` (`isSidechain: true`) | Best-effort |
| Per-day token totals, for the usage map | the same transcripts, bucketed by each message's own timestamp | Best-effort |
| Per-session totals with precomputed cost | `cost-state` entries in transcripts | Best-effort, rare (7 of 2,069 files) |
| Rate-limit rejection events | `quotaLimits` on an `assistant` line → `{status, resetsAt, rateLimitType, …}` | Best-effort, event-only (4 of 2,069 files) |
| Live token/cost stream per model and query source | OpenTelemetry: `CLAUDE_CODE_ENABLE_TELEMETRY=1` with the `console` or `prometheus` exporter | Documented |
| Running sessions | `claude agents --json` → `[{pid, cwd, kind, startedAt, sessionId, name, status}]` | Documented |
| Account tier | `~/.claude.json` → `oauthAccount.{userRateLimitTier, …}` | Best-effort |
| Long-horizon aggregates | `<config>/stats-cache.json` | Unreliable — see below |
| Per-user usage API | — the Admin API is organisation-only and explicitly unavailable for individual accounts | Unavailable |
| Pricing table | — no prices ship locally; a price snapshot must be bundled | Unavailable |

There is no `claude usage` or `claude cost` subcommand, and `/status` refuses to run headless.

### Tier 1: the status line

Claude Code invokes a user-configured status line command and passes it a JSON document on stdin
containing exactly what Altim needs, including reset times:

```json
"rate_limits": {
  "five_hour":  { "used_percentage": 53, "resets_at": 1789515600 },
  "seven_day":  { "used_percentage": 85, "resets_at": 1789549200 }
}
```

This is the only documented local source for reset timestamps, and it costs nothing. It comes with
obligations Altim must honour:

- **It requires writing to the user's `settings.json`.** Altim asks first, **merges** rather than
  overwrites, never clobbers an existing status line, and offers a one-click revert.
- The command must return in well under 100ms — Claude Code debounces at 300ms and cancels an
  in-flight script when a newer update arrives. Altim's helper writes a small state file and exits.
- Windows are dropped from the payload once their `resets_at` passes, so a missing window means
  "no data", not "zero used".
- A known defect has returned an epoch timestamp in place of a percentage before a window has data.
  Altim clamps anything above 101 and treats it as unavailable.
- Only populated after the first API response of a session, and only for subscription plans.

#### What supplies it, and what its absence costs

The command Altim registers is **its own executable with one argument**:

```
"C:\Program Files\Altim\Altim.exe" altim-statusline
```

That branch of `Program.Main` reads the payload from stdin, writes the numbers to
`<claude config>/altim-statusline.json`, and prints a line for Claude Code to render, of the shape:

```
5h 53% (3h12m) | 7d 85% (2d4h) | ctx 21% | $1.23
```

Each part appears only when the payload reported it. A session that has not had a response yet
prints `altim: no limits reported yet` rather than a row of zeroes, because Claude Code renders the
output verbatim and an empty string is a blank status bar.

**Only the numbers are written down.** The payload also carries `cwd`, `workspace.project_dir`,
`transcript_path`, `session_id` and the model; none of them reaches the state file or the printed
line. The state file is the documented payload with everything non-numeric dropped, which is what
`ClaudeStatusLineReader` is already written to expect.

**The switch is off until the user turns it on** (Settings → Providers → "Add Altim's status line to
Claude Code"). Nothing is written to Claude Code's settings on a first run or an upgrade. While it
is off, every one of these is **Unavailable**, and no substitute is invented for any of them:

| Metric | Without the status line |
|---|---|
| 5-hour and weekly **reset instants** | Unavailable. Nothing else local publishes them. Transcript `quotaLimits` events carry a `resetsAt` but appeared in 4 of 2,069 files, and only after a rejection. |
| 5-hour and weekly percentages at **Documented** grade | Drop to Best-effort, from the headless `/usage` summary's prose, which carries no reset instant and needs the network. |
| **Spend limit** | Unavailable. It has no other source at all. |
| Live session **cost** in USD | Unavailable per session. |
| **Context window** and **prompt cache** state | Unavailable. |

Measured on this machine before the helper existed: the transcript provider had **0** samples
carrying a reset time against Codex's **7,314**. The status line is the whole of that gap.

**An existing status line is refused, never replaced.** If `statusLine` is already set to something
that is not Altim's, the install writes nothing, the settings page says so, and the switch is
disabled until the user clears it themselves. Revert removes only a command carrying the
`altim-statusline` marker, so a status line Altim never touched is never removed either.

### Tier 1b: headless usage summary

`claude -p --output-format json "/usage"` runs without a session, reports `num_turns: 0` and
`total_cost_usd: 0` — it consumes no tokens and costs nothing — and returns the usage summary as
text inside `.result`, including the Opus-only and Sonnet-only weekly windows that the status line
does not expose. Altim parses it defensively with anchored patterns and treats any parse failure as
unavailable. It does reach the network, so it is disabled when the user chooses strict local-only mode.

Altim does **not** call the undocumented `/api/oauth/usage` endpoint, because doing so requires
reading the user's credential file.

### Tier 2: transcript history

Four traps, all measured, that a naive reader gets wrong:

1. **Content blocks repeat the same usage object.** One transcript had 642 assistant lines but only
   267 distinct `message.id`; summing lines overcounted output tokens by **3.15×**. Altim
   de-duplicates on `message.id` with `requestId` and `sessionId`.
2. **Subagent transcripts live in a separate directory and dominate.** Including `subagents/` took
   one session from 24.8M to 125.1M cache-read tokens — **5× more**. Repo-wide here: 22 main
   transcripts against 2,056 subagent transcripts.
3. **`<synthetic>` model entries** are locally generated placeholders and are excluded from cost.
   Model ids may carry a `[1m]` long-context suffix with its own pricing.
4. **Cache creation is split** into `ephemeral_5m_input_tokens` and `ephemeral_1h_input_tokens`,
   and the 1-hour tier — dominant in practice — bills at twice the rate. Pricing the flat
   `cache_creation_input_tokens` field under-reports.

Transcripts are pruned after ~30 days by default, so lifetime totals are not derivable from them.
Anthropic explicitly disclaims the transcript format as internal and subject to change between
versions; Altim treats every field as optional and degrades to unavailable rather than failing.

A full cold scan of 1.30 GB across 2,080 files took 6.7 seconds in plain Python; Altim scans
incrementally by modification time, so steady-state cost is negligible.

**The live token figure is a running total, so it is not a day either.** The provider merges every
transcript the incremental scanner has read into one cumulative bucket and publishes that, with no
boundary at midnight and no way to subtract the part belonging to an earlier day. Per-day figures
come instead from the same pass bucketed by each message's own timestamp; Altim's own samples
contribute that day's peak percentage only.

**Their reach is the scan's, not the account's, and it is short.** The pass reads the newest 96
transcripts written inside the last 7 days, of whatever the vendor's own pruning has left on disk,
so the days it can account for are the days those files happen to cover. On the verification machine
that was **four days**, not the thirty the pruning default would suggest, and a quieter or busier
week would give a different number again. What it is not is a fixed window Altim can quote. A day
already written keeps its row, so the map's Claude line fills forward from the first backfill rather
than reaching further back on a later one, and everything to the left of it stays unknown.

`stats-cache.json` looks authoritative and is not: on the test machine it was seven months stale
with every cost at zero, and its units have changed across versions. Altim reads it only behind a
version check, and never as a primary source.

### Paths

| Item | Windows | macOS | Linux |
|---|---|---|---|
| Config root | `%USERPROFILE%\.claude\` | `~/.claude/` | `~/.claude/` |
| Override | `CLAUDE_CONFIG_DIR` (may list several roots) | same | same |
| Transcripts | `…\projects\<slug>\<session-uuid>.jsonl` | same | same |
| Subagents | `…\projects\<slug>\<session>\subagents\agent-*.jsonl` | same | same |
| Session registry | `…\.claude\sessions\<pid>.json` | same | same |
| Global config | `%USERPROFILE%\.claude.json` | `~/.claude.json` | `~/.claude.json` |

`<slug>` is the working directory with non-alphanumerics replaced by `-`. Altim also checks
`~/.config/claude`. macOS and Linux behaviour is **unverified** — no such host was available.

### Session detection

`claude agents --json` is authoritative and already resolves liveness: a stale registry file
claimed an idle session whose process id had been recycled to an unrelated program, and the command
correctly omitted it. Process enumeration is the fallback, matching the CLI binary and excluding
helper invocations such as the browser native host.

**It is the only part of a Claude reading that starts a process, and it is expensive.** Measured
on the verification machine, one `claude agents --json` starts **105 processes and spends 5.6
seconds of CPU**, and takes 18–27 seconds of wall clock — longer than the 15 seconds Altim allows
it, so it is usually killed part way through and process enumeration answers instead. Every other
source here is a file read, which is why a refresh can be driven by a filesystem event; running
the listing on each of those tied that cost to a transcript line, and a session writing
continuously produced 173 child processes a minute.

So the listing has a floor of its own: the polling floor normally, and five minutes when the cheap
process scan cannot see anything that looks like an agent or when the previous attempt did not
answer. A skipped listing is not an empty one — the sessions the last listing reported still
stand, and only a listing that actually ran can clear them.

**Altim never stores process command lines.** Enumerating processes on the test machine exposed a
third-party tool passing an API key in plaintext in its arguments; a monitor that captured argv
would ingest other people's secrets. Altim records an executable name and a boolean, nothing more.

---

## Google Gemini CLI

**Verified against upstream source at tag `v0.60.0`** (npm `@google/gemini-cli`, published
2026-09-15), read directly for every file named below. **Not verified against a live installation**,
because `gemini` is not on this machine's path, so the findings describe what current Gemini CLI
writes rather than what was observed being written. They are graded best-effort accordingly.

What *was* established here is the layout, by listing directories and reading file timestamps and
nothing else: `~/.gemini/tmp` holds two project directories, one of which has a `chats` directory,
holding a single session file with a `.json` extension last written **2026-01-15**. That store was
written by `v0.24.0` (published 2026-01-14), and `v0.60.0` is **73 stable releases later**, so it
predates token counts in session files entirely. Altim reads it, finds no usage in it, and reports
none. That is the correct answer, and it is the path this machine exercises.

### Sources

| Metric | Source | Grade |
|---|---|---|
| Per-turn tokens: prompt, output, cached, thoughts, tool, total | `<home>/tmp/<project>/chats/**/*.jsonl`, lines where `type == "gemini"` → `tokens.{input,output,cached,thoughts,tool,total}` | Best-effort |
| Per-day token totals, for the usage map | the same lines, bucketed by each message's own `timestamp` | Best-effort |
| Per-session totals, session id, start instant | the same files: the first line's `sessionId` and `startTime`, and the turns beneath it | Best-effort |
| Subagent tokens | `<home>/tmp/<project>/chats/<parent session id>/<session id>.jsonl` | Best-effort |
| Model id | `model` on a `type == "gemini"` line | Best-effort |
| Last activity | the newest `timestamp` any line carried | Best-effort |
| Installation present | `gemini` on `PATH`, and `<home>` on disk. No process is started to decide it | Documented |
| A session running now | process named `gemini` / `gemini.exe`, or a session file written inside the activity window | Best-effort |
| **Session, daily or weekly usage percentage** | — never written to disk. See below | **Unavailable** |
| **Window length** | — same | **Unavailable** |
| **Reset instant** | — the server returns one per model, and the CLI keeps it in memory only | **Unavailable** |
| **Plan or subscription tier** | — only in `oauth_creds.json` / `google_accounts.json`, which Altim will not open | **Unavailable** |
| **Which session a running process belongs to** | — would need the process's command line, which Altim never reads | **Unavailable** |
| **Cache-write tokens** | — Gemini's response usage reports cache *reads* and never cache *writes* | **Unavailable** |
| **Cost in currency** | — no prices ship locally | **Unavailable** |

There is no `gemini usage` or `gemini quota` subcommand. `/usage` exists and is an alias of
`/stats`, which is interactive.

### The quota exists, and it is never written down

This is the whole reason Gemini has no meter in Altim, so it is worth stating exactly.

Gemini CLI really does hold a remaining-quota figure, and a good one. `/stats` calls
`Config.refreshUserQuota()`, which POSTs `retrieveUserQuota` to
`cloudcode-pa.googleapis.com/v1internal` and gets back `buckets[]` of
`{modelId, remainingAmount, remainingFraction, resetTime}` — remaining, limit and **the server's own
reset instant, per model**. That is precisely the shape Altim wants.

It lives in two private fields on the `Config` instance, `modelQuotas` and `lastRetrievedQuota`, and
goes from there to the terminal through an in-process event. **Nothing writes it to disk.** When the
`gemini` process exits, the figure is gone. So:

- There is no file for Altim to read, stale or otherwise.
- `gemini -p "/stats"` does not work as the Claude Code equivalent does. Two independent reasons:
  the non-interactive UI prints only items carrying a `text` field of type info, warning or error,
  and the stats item has structured fields and no `text`, so **nothing is printed**; and a slash
  command that returns no prompt falls through to ordinary prompt handling, so the string `/stats`
  is then **sent to the model as a prompt and spends a request**. A passive monitor must not spend
  a user's allowance to find out how much of it is left, which is the same rule that keeps Altim
  away from `codex exec --json`.
- `gemini -p "…" --output-format json` does return `stats` with a full per-model token breakdown,
  and `--output-format stream-json` returns a `result` event with the same. Both require actually
  running a turn. Same rule, same answer.

### The published allowance is in requests, and it cannot become a percentage

Google does publish a free-tier limit, which makes this the one provider where the temptation to
compute a meter is real. It has to be refused, twice over.

From the CLI's own `docs/resources/quota-and-pricing.md` at `v0.60.0`, verbatim:

> - 1000 maximum model requests / user / day
> - Model requests will be made across the Gemini model family as determined by Gemini CLI.

and, in the same file:

> Requests are limited per user per minute and are subject to the availability of the service in
> times of high demand.

The full table: Gemini Code Assist (Individual) 1,000/day; Google AI Pro 1,500/day; Google AI Ultra
2,000/day; Gemini API key free tier 250/day; Code Assist Standard 1,500/day, Enterprise 2,000/day;
Workspace AI Ultra 2,000/day.

Two things make a percentage impossible without inventing something:

1. **The denominator is unknown.** Which of 1,000, 1,500 or 2,000 applies depends on the account's
   subscription, and the only local record of that is inside `oauth_creds.json` and
   `google_accounts.json`. Altim does not open credential files, for any provider. `settings.json`
   says `security.auth.selectedType: "oauth-personal"` and nothing more, which is every one of those
   three tiers.
2. **The numerator does not exist.** The limit counts **model requests**, not tokens. A session file
   records model *turns*, and a turn is not a request. Google's own quota page says so in as many
   words: "When in agent mode or when using the Gemini CLI, one prompt might result in multiple
   model requests." Counting `type == "gemini"` lines and calling them requests would be a guess
   wearing a number's clothes.

Deriving a percentage would therefore mean guessing a denominator and estimating a numerator, which
is `AGENTS.md` rules 1 and 2 broken in one expression. Altim reports the tokens it can actually see
and says "not reported by this provider" for the percentage.

Two further reasons not to lean on the published figure even as a label: the repository contradicts
itself at the same tag (the README says the API-key free tier is 1,000 requests a day, the quota
document says 250), and the Google page the documentation links to for the individual tier no longer
carries a row for it at all — `docs.cloud.google.com/gemini/docs/quotas` lists Code Assist Standard
(1,500/day) and Enterprise (2,000/day) and no individual tier, checked 2026-09-18. The 60 requests
per minute figure appears in the gemini-cli README and in neither of the other two places.

### What the session files hold

Gemini CLI writes one append-only **JSON Lines** file per session. The first line is the file's
metadata, and the rest are records:

```jsonc
{"sessionId":"…","projectHash":"…","startTime":"…","lastUpdated":"…","kind":"main","directories":["…"]}
{"id":"…","timestamp":"…","type":"user","content":[…]}
{"id":"…","timestamp":"…","type":"gemini","model":"…","content":[…],"tokens":null}
{"id":"…","timestamp":"…","type":"gemini","model":"…","content":[…],"tokens":{"input":…,"output":…,"cached":…,"thoughts":…,"tool":…,"total":…}}
{"$set":{…}}
{"$rewindTo":"<message id>"}
```

`tokens` is the API response's own `usageMetadata`, copied field for field:
`input` is `promptTokenCount`, `output` is `candidatesTokenCount`, `cached` is
`cachedContentTokenCount`, `thoughts` is `thoughtsTokenCount`, `tool` is
`toolUsePromptTokenCount`, `total` is `totalTokenCount`. Recording is unconditional — there is no
setting that turns it off — and its only failure mode is a full disk.

**This is new.** The dormant January installation on this machine holds a single JSON document per
session with a `messages[]` array and **no token counts anywhere in it**, which is what the format
was 73 stable releases ago. Altim reads such a file, finds nothing, and reports nothing from it. The
vendor documents none of this format and is plainly willing to change it, so every field is optional
at the parse and an unexpected shape degrades one figure to unavailable rather than failing a read.

#### The units overlap, and adding them double counts

Google's API reference is explicit that `promptTokenCount` "is still the total effective prompt size
meaning this includes the number of tokens in the cached content", and that `totalTokenCount` is
"prompt + thoughts + response candidates". So Altim converts once, in `GeminiTokenBucket.ToTotals`:

| Altim component | From | Why |
|---|---|---|
| Input | `input - cached`, clamped at zero | Altim's input means *uncached* input; `input` includes `cached` |
| Cache read | `cached` | the part of the prompt served from cache |
| Output | `output + thoughts` | reasoning tokens are generated, and Google counts them inside the request total |
| Cache write | — | **null, not zero.** Gemini's response usage has no cache-creation figure at all |

The three populated components sum to `prompt + candidates + thoughts`, which is exactly
`totalTokenCount`. `tool` is read and carried but folded into none of them: Google does not say
whether tool-use prompt tokens are already inside `promptTokenCount`, and adding them could count the
same tokens twice. Under-reporting a component is preferable to inventing one.

#### Four traps a naive reader gets wrong

1. **The same message id is appended repeatedly.** The recorder writes a model turn when it
   completes, writes the whole record again when the response's usage arrives, and again when its
   tool calls are enriched. Gemini CLI's own loader keys messages by id and keeps the last. Summing
   lines therefore counts the same tokens several times over. Altim de-duplicates on message id, and
   the identity set outlives one pass so an incremental boundary cannot reintroduce the double count.
2. **The session id is on the first line only.** Message records do not repeat it, so a pass that
   resumes part way through a growing file never sees it. Each file's identity is read once and kept.
3. **A resumed session is rewritten, not appended to**, so a byte cursor into one can be stale. The
   incremental scanner re-reads a file that shrank; de-duplication on message id is what makes that
   safe.
4. **Subagent transcripts live one level deeper** and are named after the parent session rather than
   with the `session-` prefix. They are included. On Claude Code, subagent transcripts carried most
   of the token volume and outnumbered main ones 2,056 to 22.

`$rewindTo` markers are **not** applied. A rewind removes messages from the conversation Gemini CLI
will resume; it does not un-spend the tokens they cost. A usage monitor counts what was spent.

### Nothing is read outside the chats directory

`~/.gemini` holds `oauth_creds.json`, `google_accounts.json`, `mcp-oauth-tokens.json` and
`a2a-oauth-tokens.json` at its top level. Altim's Gemini reader never lists that directory and never
opens a file in it. Every enumeration it performs is **rooted at a `chats` directory**, three levels
below, so there is no path through the code that reaches a credential file rather than a filter that
avoids one. Two tests hold that line. `AFileOutsideAChatsDirectoryIsNeverRead` plants a decoy
session file, complete with token counts, at the Gemini home, at `tmp`, and in the project directory,
and asserts none of their tokens reaches the total; widening the enumeration by one level turns it
red. `ACredentialFileIsNeverOpened` holds the credential files open with `FileShare.None`, so that
reading one would throw, and asserts the reading still succeeds. The first is the load-bearing one:
the reader treats an unreadable file as one with nothing new in it, which is correct and which means
a lock alone cannot tell a read that was refused from a read that never happened.

The directory under `tmp` that holds a project's sessions is named after the project: current
versions use a slug of the project folder's own name, earlier ones a hash of its full path, and each
such directory carries a marker file holding the absolute project path. None of that is read,
returned, persisted or logged. The metadata line's `directories` field — the session's workspace
directories, as absolute paths — and its `projectHash` are discarded at the parse along with every
prompt, thought and tool result.

### Paths

| Item | Windows | macOS / Linux |
|---|---|---|
| Home | `%USERPROFILE%\.gemini` | `~/.gemini` |
| Override | `GEMINI_CLI_HOME` — replaces the **home directory**, so the result is `%GEMINI_CLI_HOME%\.gemini` | same |
| Per-project work | `<home>\tmp\<project>\` | same |
| Sessions | `<home>\tmp\<project>\chats\session-<YYYY-MM-DDTHH-MM>-<first 8 of session id>.jsonl` | same |
| Subagents | `<home>\tmp\<project>\chats\<parent session id>\<session id>.jsonl` | same |
| Legacy sessions | the same directory, one JSON document per `.json` file, no token counts | same |

macOS and Linux paths come from upstream source, not local execution: **unverified**.

### Known fragility

- The session format is undocumented and has already changed once in the way that matters most: it
  carried no token counts at all in January.
- Session pruning is a setting, not a guarantee. The documentation says the default policy keeps 30
  days; the settings object it lives on defaults to absent in the source, and the cleanup pass runs
  only for the project a session is started in. Altim depends on neither reading: its scan is bounded
  by its own window and file caps, and a day it cannot account for stays unknown.
- The project directory naming changed from a path hash to a slug, with a migration at start-up, so
  both layouts can be present at once. Altim does not read either name, which is why the change costs
  it nothing.
- An npm-shim install runs the CLI as `node`, so the process scan will not see it. File recency is
  the fallback signal and is why the provider does not depend on the scan alone.
- `gemini --version` honours a `CLI_VERSION` environment variable, so it is not a trustworthy
  version signal. Altim does not run it.

### Deliberately not read

| Source | Reason |
|---|---|
| `retrieveUserQuota` on `cloudcode-pa.googleapis.com` | it is an undocumented internal endpoint and calling it directly would mean reading the user's OAuth credentials, which Altim does not do for any provider |
| `gemini -p "…" --output-format json` turn stats | obtaining them means running a turn and spending a model request |
| `gemini -p "/stats"` | prints nothing, then sends `/stats` to the model as a prompt |
| `--list-sessions` | starts a process, lists only the current working directory's project, and reports no usage |
| OpenTelemetry to a local file (`telemetry.enabled`, `telemetry.outfile`) | genuinely carries `gemini_cli.token.usage` per model, but telemetry is **off by default**, turning it on means writing to the user's `settings.json` and restarting their CLI, and `logPrompts` defaults to **true** so the file it produces would contain the user's prompts. The same reasoning keeps Altim away from Claude Code's OTLP export |
| `logs.json` | user prompts only: `{sessionId, messageId, timestamp, type, message}`. No usage of any kind |
| Checkpoints and the `history/` shadow git repository | conversation state and file contents, no token counts |
| `settings.json`, `projects.json`, `.project_root`, `installation_id` | configuration and identity. None carries usage, and two of them map a directory back to a project path |
| `oauth_creds.json`, `google_accounts.json`, `mcp-oauth-tokens.json` | credentials and account identity. Never opened, for any reason |

---

## Day backfill: what fills the usage map

The map draws one square per provider per local calendar day, and a day's token figure comes only
from a source that reports whole days. Altim's own readings are running totals rather than daily
amounts, for the reasons given under each provider above, so they supply that day's peak percentage
and never its volume. Measured **2026-09-17**, on the machine the rest of this document was verified
against.

| Source | Reach | Grade | Notes |
|---|---|---|---|
| Codex daily buckets, `account/usage/read` | the buckets the reply carries: 100 used days spread over about nine months here | Best-effort | one gated network call, at most daily, with the five-minute fallback above; one undifferentiated figure per day, recorded as input with the other three components unreported |
| Claude Code transcripts, bucketed by each message's timestamp | what the scan covers: the newest 96 transcripts written in the last 7 days, four days of usage here | Best-effort | local only, starts no process and makes no network call; reuses the incremental scanner and its cursors; reports all four components |
| Gemini CLI session files, bucketed by each model turn's timestamp | what the scan covers: the newest 96 session files written in the last 7 days, across every project directory | Best-effort | local only, starts no process and makes no network call; reports input, output and cache read, and never cache write, which Gemini does not report at all |
| Altim's own samples | from install | Best-effort | supplies the day's **peak percentage only**, never its tokens |

**No reach here is a number Altim can promise.** The Codex figure counts days that had usage rather
than days in a window, so the span is far wider than the count; the Claude figure is whatever the
newest transcripts still on disk happen to cover, which is days rather than months; the Gemini
figure depends on a retention policy that is a setting rather than a guarantee and that only runs
for projects the user reopens. All three move with how the account was used. A backfill runs at most
once a day per provider, is skipped entirely when its source is unavailable, and leaves the days it
cannot account for unknown rather than zero.

**A Gemini day carries tokens and never a peak.** No Gemini source reports a percentage at all, so
that provider's row of the map is drawn from volume alone and its peak stays unknown for every day,
including days Altim watched. That is the same distinction the map already makes everywhere else:
unknown is not zero.

**A day row is merged field by field**: its token figure comes from a per-day source above, its peak
from Altim's samples, and neither writer can erase the other's field. The first run here wrote **104
day rows**, 100 from Codex and 4 from the transcripts. `codex 2026-09-17` carried 211,532,486
backfilled tokens beside a 74% peak Altim had watched itself, while `codex 2026-09-15` carried
tokens and no peak at all, because Altim was not running that day. The tooltip names the source, so
a backfilled figure is never read as something Altim watched happen.

## Implementation status

Wired up today:

| Provider | Reading |
|---|---|
| Claude Code | status-line state file (documented), headless `/usage` summary, transcript token history including subagents, per-day totals from that same pass, `claude agents --json` sessions |
| Codex | app-server `account/rateLimits/read` and `account/usage/read`, day backfill with a five-minute bounded fallback to the last reading, rollout tail fallback, read-only state database for recent threads, `codex doctor --json` for paths and auth mode |
| Gemini CLI | session-file token history including subagents, per-day totals from that same pass, per-session totals with model and start instant, presence and activity. **No quota metric of any kind**, because none exists locally |

Deliberately not read yet, and why:

| Source | Reason |
|---|---|
| `codex exec --json` turn usage | obtaining it means *running* a turn; a passive monitor must not spend a user's quota |
| `account/rateLimits/updated` push | needs a long-lived subscription, which conflicts with one scheduler owning all timing. Polling is gated at 60s instead |
| Claude Code OpenTelemetry export | requires injecting environment variables into the user's tool and running a collector |
| Gemini CLI OpenTelemetry export | off by default, would mean editing the user's `settings.json` and restarting their CLI, and its prompt logging defaults to on, so the file it produced would hold their prompts |
| Gemini CLI `retrieveUserQuota` | the one source with a real remaining figure and reset instant, and the only way to reach it is to read the user's OAuth credentials and call an undocumented internal endpoint. Altim opens no credential file, for any provider |
| Gemini CLI headless `/stats` and `--output-format json` | the first prints nothing and then spends a model request sending `/stats` to the model; the second requires running a turn |
| `cost-state` and `quotaLimits` transcript entries | present in 7 and 4 files out of 2,069; too rare to build on, useful later as a cross-check |
| `stats-cache.json` | measured seven months stale with zero costs; a version gate is not worth the wrong-number risk |
| session registry files | observed claiming an idle session whose process id had been recycled; the CLI's own listing is authoritative |
| Compressed rollouts (`.zst`) | skipped rather than guessed at |
| Account tier from the global config | not needed until Altim shows plan-specific limits |

Helper processes can only be excluded by executable name, not by inspecting arguments, because
reading another process's command line would ingest other tools' secrets. That is why session
detection prefers each vendor's own listing command.

## Privacy: what Altim reads and ignores

Session files contain prompts, model reasoning, file contents, command output, patches, repository
URLs and working directories. Altim's readers are built to be structurally incapable of carrying
that anywhere:

- Only numeric and timestamp fields are parsed out of a line: `*_tokens`, `used_percent`,
  `window_minutes`, `resets_at`, `plan_type`, `limit_id`, timestamps. Every other field is discarded
  before the line leaves the reader.
- Raw lines are never persisted, logged or included in diagnostics.
- Credential files are never opened. Auth state is learned only from a redacted CLI report.
- Project and repository identifiers are not stored in the usage history database.

Gemini CLI adds two of its own, because its store is laid out differently from the other two:

- **Its session files sit three levels below the directory its credentials do**, so the reader's
  enumerations are rooted at a `chats` directory rather than at the Gemini home and filtered on the
  way down. There is no path through that code which reaches `oauth_creds.json`.
- **Its metadata line carries the session's workspace directories as absolute paths**, beside a name
  for the project. Both are discarded at the parse, along with the prompts, the model's reasoning,
  the tool arguments and the tool output that fill the rest of the file.

See [PRIVACY.md](PRIVACY.md) for the user-facing statement.
