# Reference audit

The supplied design mockup is the target. This was an element-by-element comparison against the
build as of the first running version, and the list of changes required to match it. Everything
below is now **built**, except where the last section says otherwise and why.

Where the reference and `DESIGN.md` disagreed, **the reference won** and `DESIGN.md` was amended:
the mockup uses an uppercase eyebrow above the page title, 12px radii on cards and panels, and a
card whose metrics are all one figure size. All three were previously ruled out. The radius scale
gained `12` for cards, panels and the popup and `4` for chips; 6 and 8 still apply to controls,
rows and drop downs.

## Dashboard shell

| Element | Reference | Built |
|---|---|---|
| Sidebar width | 192, `SurfaceMuted`, 1px right border | ✅ — owned by the container, not the list, so the header and footer stand on the same ground |
| Brand header | mark 22px + wordmark, 24px inset, 64 tall, above the nav | ✅ — the Altim mark, drawn as an opacity mask over `TextPrimary` so it is ink in both variants |
| Nav icons | 16px icon per row: home, provider glyph, provider glyph, clock, gear | ✅ — drawn, not imported; the style supplies the path so no view model holds a geometry |
| Nav row | 40 tall, radius 8, 12px gap, 14px label, flat selected fill, no bar, no bold | ✅ |
| Sidebar footer | wordmark + two quiet lines, pinned bottom, 24px inset | ✅ — "Altim" and Altim's own tagline, "AI usage, at a glance." |

## Overview header

| Element | Reference | Built |
|---|---|---|
| Eyebrow | greeting, 11px, tracked 0.08em, grey, capitals | ✅ |
| Title | "Usage overview", 40/44, 700 | ✅ — and every other page title with it |
| Subtitle | one grey 14px line | ✅ — "Here's how your AI agents are doing." |
| Status, top right | 6px dot + the aggregate status sentence | ✅ — `UsageAggregator`'s own sentence, so the panel and the page cannot disagree |

## Provider cards

| Element | Reference | Built |
|---|---|---|
| Layout | two columns, 24px gap, equal width | ✅ — `CardGrid`, dropping to one column below a 320 column |
| Card | radius 12, 1px border, 24px padding | ✅ |
| Header | 28px glyph, 21px name, 16px chevron; status dot + word right | ✅ — the chevron opens that provider's page |
| Metric | name + figure + meter, one figure size for the set | ✅ |
| Meter | 8px tall, radius 4 | ✅ — the rail is the reference's; the graduated scale under it and the threshold index through it are Altim's own, added because a reading measured against a ceiling should read like an instrument. See `DESIGN.md`. |
| Footer | 1px rule, two cells split by a vertical hairline: "Resets in" and "Pacing" | ✅ — plus the last refreshed caption at the far end of the same row, and the footer pushed to the foot of the card so two cards in a row line up |

**Pacing** is a real computation: this window's level against the same point in the previous
window, from local history. With too little history it reads `—`, never a guess.

## Bottom row

| Element | Reference | Built |
|---|---|---|
| Usage history panel | heading, range pill, legend dots, y labels, gridlines, 2px lines, 4px dots, dated x axis | ✅ — and the same panel is the body of the History page, so the two surfaces cannot drift |
| Agent activity panel | heading, badge, rows: glyph, name, status line, chip, relative time, dot | ✅ — see the privacy note below for the chip and the badge |

## Popup

| Element | Reference | Built |
|---|---|---|
| Header | mark + wordmark left, gear right | ✅ — the gear opens the dashboard on Settings |
| Provider row | 28px glyph, name + chevron, one compact line `5h 42% · 7d 68%`, no meters | ✅ |
| Resets | "Resets in" with one line per provider | ✅ — the soonest window each provider reports |
| Status | 18px status icon + sentence | ✅ |
| Action | "Open Altim →" as text with an arrow, left aligned | ✅ |
| Sections | separated by 1px rules, 16px padding | ✅ |

## What the reference asks for that Altim will not do

**The activity chips cannot show what the mockup shows.** Its chips carry a file being edited and
a shell command being run. Altim's providers are built so that neither can be obtained: process
arguments are never read — other tools leak secrets there — and the parsers reject any string
shaped like a path. The rows carry what can honestly be reported — the provider, the state, how
long the session has been running, its token count — and the chip carries the model identifier,
which is the one descriptor a provider exposes that is safe to show. A session that does not name
a model gets no chip rather than a placeholder. This is a deliberate privacy boundary, not an
oversight; ask before widening it.

**The activity panel's badge is a label, not a picker.** The mockup draws "Live" as a drop down.
Activity is read live and never written down, so there is no second span to offer, and a drop
down whose list has one entry in it is a control that lies about what it can do. It is drawn as
the same pill without a chevron.

**There are no per-metric token sub-lines.** The mockup sets `~ 56K / 130K tokens` under each
metric. Altim's providers report tokens per provider rather than per window, and no provider
reports a token *limit* at all, so there is no used-against-limit pair to print. The card carries
the one token count that is reported, as a caption under the metrics.

**Pacing and reset times read `—` when there is nothing to compare or nothing reported.** The
mockup has a figure in every cell because a mockup can. A percentage Altim cannot source is an em
dash, and a reset instant a provider does not report is an em dash.

## Remaining visual differences from the reference

These are the differences still visible with the screenshots beside the mockup:

1. **Provider and metric names are Altim's, not the mockup's.** "Claude Code" rather than
   "Claude", and `Session` / `Weekly` / `GPT-5.3-Codex-Spark 5 hour` rather than "Session usage" /
   "Weekly usage". The labels come from the window length via `LimitWindowClassifier` or from the
   provider itself, and inventing friendlier ones would be inventing.
2. **Codex reports three windows, so its card is taller than the mockup's two-metric card.** The
   grid arranges both cards at the row's height, so the Claude card carries the difference as
   whitespace above its footer.
3. **The status sentence is "All providers operational", not "All systems operational."** Altim
   has no systems; it has providers, and the sentence is the aggregator's.
4. **Pacing above the previous window is neutral rather than coloured.** The mockup colours `-8%`
   green and leaves `+12%` in the ink; Altim does the same, which means most of the time the cell
   is ink. Colouring it red would be a threshold warning the meter has not reached.
5. **The bottom row is aligned with the cards above it.** The mockup's two bottom panels are
   slightly wider and narrower respectively than the cards above them, and do not line up with
   them; that reads as an artefact of the drawing rather than a decision.
6. **The window is 1120×900, not the mockup's ~1170×880.** Three windows on the Codex card need
   the extra height to fit without scrolling.

## Mockup artefacts, not design decisions

- The bottom row's panels not lining up with the cards above them (see 5).
- The provider name in the mockup's popup is "Claude" while its card says "Claude" too; the real
  provider names are longer, and the popup and the card use the same one.
- The mockup's tray icon and window chrome are the host platform's, not the product's.

## Not in the reference, kept anyway

The provider pages, the Settings sections, the empty and error states and the unavailable copy
have no counterpart in the mockup. They stay as built, with one change for coherence: their page
titles now use the same `Display` role as Overview and History, because a window whose pages set
their own title sizes reads as several applications.
