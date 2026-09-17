# Usage map and combined usage — design

**Status:** approved for planning, 2026-09-17.

A calendar of daily usage, one square per day, per provider, with a combined row beneath them.
The map answers *how much did I use, and when*; the tooltip answers *did I run out*.

## Why this is buildable today

Altim has kept usage history only since it was installed, which would make a year-long map empty
for a year. Both providers can hand us the past:

- **Codex** returns roughly 97 daily buckets from `account/usage/read`, each a date and a token
  count. The client already calls this and currently only *counts* the array.
- **Claude Code** transcripts carry per-message usage with timestamps, and Altim already scans them
  incrementally with de-duplication on message id. Daily totals fall out of one pass, bounded by
  the provider's own ~30-day retention.

So the map is useful the day it ships rather than three months later.

## What a square means

One row per provider, one square per local calendar day.

**Value:** total tokens attributed to that day — input + output + cache read + cache write, summed.
The four components are stored separately and broken out in the tooltip, because a day dominated by
cache reads is not the same kind of day as one dominated by output, and the summed figure alone
hides that.

**Tooltip:** the date, the four token components, the day's **peak quota** (the highest percentage
any of that provider's windows reached that day), and whether the row was observed or backfilled.

**Peak quota is per provider and is never combined.** Forty per cent of a Claude weekly window and
forty per cent of a Codex weekly window are different quantities measured against different limits;
adding or averaging them would invent a number. The combined tooltip lists each provider's peak on
its own line.

### Zero and unknown are different squares

| State | Meaning | Rendering |
|---|---|---|
| Unknown | No data for that day: before install, outside backfill reach, or a provider that was not installed | hairline outline, no fill |
| Zero | We have data and it says nothing was used | faintest fill in the ramp |

This distinction is the product's whole position on honesty, and it is the one rendering detail that
may not be compromised for visual tidiness.

## Colour scale

**Quantile buckets, not linear.** Token counts span orders of magnitude — a day of heavy cache reads
can be fifty times a day of ordinary work — so a linear ramp renders every normal day as the palest
square and one day as black. Days with data are ranked and split into five buckets against the
user's own history; the scale is therefore relative to them and not to any absolute token figure.

The ramp is five steps of `TextPrimary` opacity from the design system, monochrome in both themes.
Provider accent colour stays where `docs/DESIGN.md` puts it — identity only, never data.

Bucketing runs over the visible range. Edge cases that must be specified rather than discovered:
fewer days with data than buckets, all values identical, and a single day with data all collapse to
one non-empty bucket rather than producing empty ranges.

## Data sources and precedence

| Source | Reach | Grade | Notes |
|---|---|---|---|
| Codex daily buckets | ~97 days | best-effort | one network call, subject to the existing permission setting and rate gate |
| Claude transcript scan | ~30 days | best-effort | local only; reuses the incremental scanner |
| Altim's own samples | from install | best-effort | rolled up nightly and on demand |

**Precedence: observed beats backfilled.** A day Altim watched itself is authoritative; a backfilled
row fills gaps and never overwrites an observed one. Each row records which it is, so a later
backfill cannot silently rewrite history and the tooltip can say where the number came from.

**Backfill runs once per provider**, on a schedule no tighter than daily, and is skipped entirely
when its source is unavailable — no network permission, no CLI, an unreadable store. A skipped
backfill leaves unknown squares, which is the correct answer.

## Storage

Migration 2 adds one table. It does not touch `usage_sample`.

```sql
CREATE TABLE usage_day (
  provider_id   TEXT    NOT NULL,
  day           TEXT    NOT NULL,           -- local calendar day, ISO yyyy-mm-dd
  input_tokens        INTEGER,
  output_tokens       INTEGER,
  cache_read_tokens   INTEGER,
  cache_write_tokens  INTEGER,
  peak_percent  REAL,                        -- nullable: unknown stays unknown
  source        TEXT    NOT NULL,            -- 'observed' | 'backfilled'
  updated_at    INTEGER NOT NULL,
  PRIMARY KEY (provider_id, day)
);
```

Nullable columns mean unreported, exactly as in `usage_sample`. A row exists only for a day we know
something about; a missing row is the unknown square.

**Retention: indefinite.** Two providers over a year is roughly 40KB, three orders of magnitude
below the raw-sample budget, so unlike samples these are never down-sampled or pruned. The map is
the long memory; the samples are the short one.

**The day is the user's local calendar day**, resolved when the row is written, and stored as text
rather than an instant so a timezone change cannot silently re-bucket history.

## Rollup

Daily rows are derived from `usage_sample` for days Altim observed. Token totals in a sample are
cumulative per reading, so the day's value is the **last reading of that day**, not a sum of
readings — the same trap the retention pass got wrong and had to be fixed for. Peak percent is the
maximum across that day's samples for that provider.

The rollup must be idempotent: recomputing a day produces the same row, and recomputing an observed
day never demotes it to backfilled.

## Interface

`IUsageHistoryService` gains day-granularity reads:

```csharp
ValueTask<IReadOnlyList<UsageDay>> GetDaysAsync(string providerId, DateOnly from, DateOnly to, CancellationToken ct);
ValueTask UpsertDaysAsync(IReadOnlyList<UsageDay> days, CancellationToken ct);
```

Providers gain an optional capability for history, so a provider that cannot backfill simply does
not implement it and the UI needs no per-provider knowledge:

```csharp
public interface IUsageHistorySource   // implemented by a provider that can reach into the past
{
    ValueTask<IReadOnlyList<UsageDay>> GetHistoryAsync(DateOnly from, DateOnly to, CancellationToken ct);
}
```

## Interface and UI

The map lives on the **History page**, above the existing tape, which keeps its range picker and
intraday detail. Layout: one labelled row per provider, then a separator, then the **Combined** row
summing tokens across providers.

- Squares sized from the spacing scale, with the week as the vertical axis and months labelled along
  the top, in the manner the design system already sets for the tape's axes.
- Hover and keyboard focus both show the tooltip; the grid is reachable by keyboard and each square
  carries an accessible name reading the date and the value, since a grid of unlabelled rectangles
  is invisible to a screen reader.
- Empty state, when no provider has a single day of data: the documented sentence, not an
  illustration and not a grid of empty squares pretending to be a map.
- Both themes designed, per the existing token rules.

## Privacy

No new class of data. Days, token counts and percentages only — no project, path, prompt or command
ever reaches this table, and the backfill readers parse the same numeric fields the live readers do.
Coarsening to a day is strictly less revealing than the samples already stored. `PRIVACY.md` gains a
line stating that daily totals are kept indefinitely while samples are not.

## Performance

- Backfill is off the UI thread, bounded, and runs at most daily per provider.
- The Claude pass reuses the incremental scanner and its cursor, so only the first run reads far.
- Reading a year of days is 730 rows; no paging, no cache beyond the view model.
- The map must not add to idle cost: no timer of its own, folded into the existing maintenance pass.

## Testing

- Rollup: cumulative tokens take the day's last reading, not a sum; peak is the day's maximum;
  recomputation is idempotent; an observed day is never demoted by a later backfill.
- Quantiles: all-zero history, one day, identical values, fewer days than buckets, one extreme
  outlier not flattening the rest.
- Rendering: unknown and zero are visually distinct (a pixel assertion, as with the other
  design-system rules); both themes; the combined row sums tokens and never combines percentages.
- Backfill: merge does not double-count a day already observed; a provider with no history source is
  simply absent; a failed or skipped backfill leaves unknown squares and says so.
- Storage: migration 2 applies forward-only, an older database upgrades cleanly, and a day row
  round-trips its nulls as nulls.

## Out of scope

Cost in currency (no local price source, and the one figure Claude reports is per session), hourly
maps, streak counts and "best day" statistics, export, and any cross-provider *percentage*. Each is
a separate request if it is ever wanted.

## Risks

1. **Token volume is not effort.** The map reads as productivity and is not; the tooltip's component
   breakdown is the mitigation, and the README should say so plainly rather than let the visual imply
   otherwise.
2. **Backfill reach differs per provider**, so the left edge of one row is older than another's. The
   unknown rendering makes this visible rather than hiding it behind zeros.
3. **The Codex bucket field names are undocumented** and were inferred; a shape change degrades to no
   backfill, which must read as unknown rather than as zero.
4. **A timezone move** re-buckets future days but not past ones. Accepted, and stated here rather
   than silently reconciled.
