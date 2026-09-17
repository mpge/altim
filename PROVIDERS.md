# Providers: where every number comes from

Altim never invents a metric. Every value on screen is traced to a source in this document.
If a source does not report something, the UI shows **"Not reported by this provider"** — not a
zero, not an estimate.

Each source is graded:

- **Documented** — vendor-documented interface, stable to depend on.
- **Best-effort** — real and verified, but undocumented or experimental; may break on a vendor update.
- **Unavailable** — cannot be obtained locally today. Modelled in the provider contract so it can
  light up later without UI changes.

Verification date: **2026-09-15**. Findings below were executed against a live installation on
Windows 11 unless marked unverified.

---

## OpenAI Codex

Verified against `codex-cli 0.153.4` (npm `@openai/codex`), ChatGPT authentication, 2,518 local
sessions.

### Sources

| Metric | Source | Grade |
|---|---|---|
| Live usage %, window length, reset time, per limit family | `codex app-server` JSON-RPC → `account/rateLimits/read` | Best-effort |
| Plan type, credit balance, reset credits | same response: `planType`, `credits`, `rateLimitResetCredits` | Best-effort |
| Daily token history (97 buckets), lifetime tokens, streaks | `codex app-server` JSON-RPC → `account/usage/read` | Best-effort |
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
daily figures therefore come from `account/usage/read`'s ~97 daily buckets and from nowhere else;
Altim's own samples contribute that day's peak percentage only.

**The backfill reaches back ~97 days and answers from the last reading when the gate is shut.** It
makes the same gated `account/usage/read` call as the live meter, and on a real machine it almost
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
come instead from the same pass bucketed by each message's own timestamp, bounded by the ~30-day
transcript retention; Altim's own samples contribute that day's peak percentage only.

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

## Implementation status

Wired up today:

| Provider | Reading |
|---|---|
| Claude Code | status-line state file (documented), headless `/usage` summary, transcript token history including subagents, `claude agents --json` sessions |
| Codex | app-server `account/rateLimits/read` and `account/usage/read`, day backfill with a five-minute bounded fallback to the last reading, rollout tail fallback, read-only state database for recent threads, `codex doctor --json` for paths and auth mode |

Deliberately not read yet, and why:

| Source | Reason |
|---|---|
| `codex exec --json` turn usage | obtaining it means *running* a turn; a passive monitor must not spend a user's quota |
| `account/rateLimits/updated` push | needs a long-lived subscription, which conflicts with one scheduler owning all timing. Polling is gated at 60s instead |
| Claude Code OpenTelemetry export | requires injecting environment variables into the user's tool and running a collector |
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

See [PRIVACY.md](PRIVACY.md) for the user-facing statement.
