# Reference audit

The supplied design mockup is the target. This is an element-by-element comparison against the
build as of the first running version, and the list of changes required to match it.

Where the reference and `DESIGN.md` disagree, **the reference wins** and `DESIGN.md` is amended:
the mockup uses an uppercase eyebrow above the page title, and 12px radii on cards and panels.
Both were previously ruled out. The token scale gains `12` for cards, panels and the popup; 6 and 8
still apply to controls and meters.

## Dashboard shell

| Element | Reference | Build | Action |
|---|---|---|---|
| Sidebar width | 192, `SurfaceMuted`, 1px right border | 200, correct ground | narrow to 192 |
| Brand header | mark 22px + wordmark 16px semibold, 24px inset, ~64 tall, above the nav | **missing** | add |
| Nav icons | 16px icon per row: home, provider glyph, provider glyph, clock, gear | only provider glyphs | add home, clock, gear |
| Nav row | 40 tall, radius 8, 12px gap, 14px label, selected = flat `#EDEDED` fill, no bar, no bold | 32 tall, no icons | resize, add icons |
| Sidebar footer | wordmark 13 semibold + two 12px grey lines, pinned bottom, 24px inset | **missing** | add, with Altim's own tagline |

## Overview header

| Element | Reference | Build | Action |
|---|---|---|---|
| Eyebrow | "GOOD AFTERNOON", 11px, tracked 0.08em, grey | greeting used as the title | add as eyebrow |
| Title | "Usage overview", 40/44, 700, tracking -0.02em | "Good morning" at title size | retitle |
| Subtitle | one grey 14px line describing the page | "Here's how your AI agents are doing." | keep, restyle to 14px grey |
| Status, top right | 6px green dot + 12.5px "All systems operational" | **missing on Overview** | add, bound to real aggregate status |

## Provider cards

| Element | Reference | Build | Action |
|---|---|---|---|
| Layout | **two columns**, 24px gap, equal width | full-width, stacked | two-column grid, wrapping to one column when narrow |
| Card | radius 12, 1px border, 24px padding, white | radius 8, 20px padding | adjust |
| Header | 28px glyph, 21px semibold name, 16px chevron; status dot + word right | glyph + name + status, no chevron | add chevron (navigates to the provider page) |
| Metric label | 14px name with a 12px grey sub-line: `~ 56K / 130K tokens` | label only; tokens shown once per card | move token figures under each metric that reports them |
| Metric value | 30px semibold, right aligned | correct | keep |
| Meter | 8px tall, radius 4 | 6px tall | thicken to 8 |
| Footer | 1px rule, two columns split by a vertical hairline: "Resets in" / `5h 12m`, and "Pacing" / `+12%` / "vs. last week" | single "Updated" line | build the two-column footer; keep "Updated" as the card's last refreshed caption |

**Pacing** is a real computation, not decoration: this window's usage rate against the same point in
the previous window, from local history. With too little history it reads `—`, never a guess.

## Bottom row — both panels are missing entirely

**Usage history panel.** Title 15px semibold, a range dropdown pill on the right (32 tall, radius 8,
1px border, 13px label + chevron), legend dots (8px) naming each provider, then the tape: y labels at
100/50/0%, hairline gridlines, one 2px line per provider, 4px dots, x axis of dates. This duplicates
the History page deliberately — the reference shows the last 7 days on Overview.

**Agent activity panel.** Title + "Live" dropdown. Each row: 20px provider glyph, 14px semibold name,
13px grey status line, a monospace chip (11px, `SurfaceMuted` ground, radius 4, 4×8 padding), and on
the right a 12px relative time plus an 8px status dot.

> **One item cannot be built as drawn.** The reference chips show a file being edited and a command
> being run. Altim's providers are built so that information cannot be obtained: process arguments are
> never read (other tools leak secrets there) and the parsers reject any string that looks like a path.
> The rows will therefore carry what we can honestly report — state, duration, token count — and the
> chip only appears when a provider genuinely exposes a safe descriptor. Ask me before widening this;
> it is a deliberate privacy boundary, not an oversight.

## Popup

| Element | Reference | Build | Action |
|---|---|---|---|
| Header | mark + "Altim" wordmark left, settings gear right, 16px padding | **missing** | add |
| Provider row | 28px glyph, 15px semibold name + chevron, one compact line `5h · 42% · 7d 68%` with bold values and grey labels — **no meters** | full metric rows with meters | restructure to the compact line |
| Resets | section labelled "Resets in" with `5h 12m (Claude)` per provider | lists every metric key | group per provider |
| Status | 18px check-circle icon + 13px sentence | text only | add the icon |
| Action | "Open Altim →" as text with an arrow, left aligned | full-width outlined button | restyle |
| Sections | separated by 1px rules with 16px padding | correct | keep |

## Not in the reference, keep anyway

The provider pages, Settings sections, the empty and error states, and the unavailable copy have no
counterpart in the mockup. They stay as built.
