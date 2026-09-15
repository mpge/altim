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

## Anthropic agentic CLI

Research in progress — this section lands with the same level of evidence before the provider is
implemented.

---

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
