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
| Altim's own samples | from install | best-effort | supplies the day's **peak percentage only**, never its tokens |

**Token figures come from the per-day sources only.** Altim's own samples contribute the day's peak
percentage and never its token count, for the reason given under Rollup: the readings they carry are
running totals rather than daily amounts. Each row records where its token figure came from, so the
tooltip can say.

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

**A day's token figure comes only from a per-day source. The samples supply the peak percentage and
nothing else.**

This was written the other way round first, and a run against real data disproved it. A sample's
token figure is not a daily amount: the transcript provider reports a cumulative total over
everything it has scanned, and the Codex provider reports a sum over a bounded window of the most
recent session files, which visibly oscillates between two values inside the same minute. The
largest such reading seen during a day is a running total that happened to be observed that day, not
the tokens spent in it. Summing readings is wrong, taking the last is wrong, and taking the highest
is wrong, because none of the readings is ever a per-day quantity in the first place.

Both providers do expose true per-day token figures, through the backfill sources below. Those are
the only source of a square's value.

A percentage is different in kind. Each reading is a point-in-time measurement against a live
window, so the largest one seen during a day is a real fact about that day: this is how close to the
limit the user came. That is what the rollup keeps, normalised through `UsagePercent.Normalise`.

The rollup must be idempotent: recomputing a day produces the same row.

### Precedence is per field, not per row

A row is written by two different writers that never overlap:

| Field | Written by | Rule |
|---|---|---|
| The four token components, and `source` | backfill only | a later backfill replaces an earlier one; a row that reports no tokens leaves them alone |
| `peak_percent` | the sample rollup only | the day's maximum, and a later rollup never lowers it |

So an upsert merges rather than replacing. A write that carries no tokens must leave the tokens
already there untouched, and a write that carries no peak must leave the peak alone. The earlier
rule — "observed beats backfilled", whole row — is what allowed a rollup to erase a correct
per-day figure with a running total, and it is retired.

`source` describes where the **token figure** came from, because that is the number the square
draws. A day that has a peak and no tokens draws as unknown: we know how close to the limit the user
came, and not how much they spent.

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

- Rollup: a rolled-up day carries a peak and no tokens, whatever its samples reported; peak is the
  day's maximum; a rollup never erases or lowers a backfilled token figure, and a backfill never
  erases a peak;
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
