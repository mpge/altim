# Altim design system

The contract every view implements. If a value is not here, it does not belong in a view —
add it here first, then reference the token.

Where this document and the supplied design reference disagreed, the reference won and this
document was amended to match it. Those amendments are marked **(reference)** where they
overturn something this document previously ruled out: the uppercase eyebrow, the 12px radius
on cards and panels, and a card whose metrics are all one figure size.

## Point of view

Altim is an instrument, not a dashboard. It reports a level and a time, then gets out of
the way. Two ideas carry the identity:

1. **Numbers are the subject.** The percentage is the largest thing on screen; labels stay
   small and quiet. All figures set in tabular numerals so digits do not shift as they tick.
2. **The altitude tape.** The logo is an ascending mark, the name reads as altimeter. Meters
   are flat rails with a hairline limit tick, history is a tape with hairline level lines at
   0/50/100. No rounded candy bars, no gradient fills, no shadowed cards stacked on cards.

Restraint rules: separators and whitespace instead of nesting cards; one accent per surface;
colour carries provider identity and status only, never decoration.

## Colour

Light (from the brief, exact):

| Token | Value | Use |
|---|---|---|
| `Surface` | `#FFFFFF` | window background |
| `SurfaceMuted` | `#FAFAFA` | popup background, hover rows, chart ground |
| `TextPrimary` | `#0A0A0A` | figures, headings, body |
| `TextSecondary` | `#666666` | labels, captions, secondary metrics |
| `Border` | `#EAEAEA` | 1px borders, separators, meter track |

Dark — designed, not inverted. Backgrounds lift rather than invert, borders stay lower
contrast than text, pure black is never used as a surface:

| Token | Value | Use |
|---|---|---|
| `Surface` | `#0C0C0D` | window background |
| `SurfaceMuted` | `#151517` | popup background, hover rows, chart ground |
| `TextPrimary` | `#F2F2F3` | figures, headings, body |
| `TextSecondary` | `#8A8A90` | labels, captions, secondary metrics |
| `Border` | `#232326` | 1px borders, separators, meter track |

Meter fill is `TextPrimary` in both themes. Status and provider colour:

| Token | Light | Dark | Use |
|---|---|---|---|
| `StatusOk` | `#15803D` | `#4ADE80` | operational dot, healthy, pacing under the last window |
| `StatusWarn` | `#B45309` | `#FBBF24` | threshold reached |
| `StatusError` | `#B42318` | `#F87171` | provider unreachable |
| `AccentAnthropic` | `#C05B32` | `#D2764D` | provider glyph and dot only |
| `AccentOpenAI` | `#0A0A0A` | `#F2F2F3` | provider glyph and dot only |

A provider accent never fills a meter, a button, or a background. It marks identity at 16,
20 or 28px and nothing else. Status colour appears as a 6px or 8px dot, a single word, or the
disc of the panel's status icon — never as a tinted panel.

One figure carries status colour: **pacing** below zero is `StatusOk`, because it is the one
piece of good news the interface has to report and the reference colours it. Pacing at or above
zero stays in `TextPrimary`. It is deliberately not `StatusWarn`: a window can be pacing above
the last one and still be nowhere near its limit, and the meter already says when it is not.

A *filled* control is not the same thing as an accent. The one primary action on a surface —
"Retry", "Open Altim" — fills with `TextPrimary` and sets its label in `Surface`: the figure
ink and the page ground, both already in the palette. One per surface.

## States

Interaction runs along the same ramp the palette already has, and which step it uses depends
on the ground the control is standing on. `SurfaceMuted` is the hover for anything on
`Surface`. It is invisible on a muted ground, because it *is* that ground, so anything
standing on `SurfaceMuted` — the popup, the sidebar, a drop down — hovers to `Border`
instead, one step further along. That leaves `Surface` free to mean selected on a muted
ground, and keeps hover and selected legibly apart.

| Ground | Hover | Selected |
|---|---|---|
| `Surface` | `SurfaceMuted` | `SurfaceMuted` |
| `SurfaceMuted` | `Border` | — |
| Sidebar (`SurfaceMuted`) | `SidebarHover`: `#FFFFFF` light / `#1C1C1F` dark | `SidebarSelected`: `#EDEDED` light / `#232326` dark |

The sidebar is the one ground carrying both a hover and a selection, so it owns two keys of its
own **(reference)**. Light presses the selected row *into* the muted ground and lifts the
hovered one *out* of it; Dark can only lift, so it lifts the selected row further than the
hovered one. Either way the selected row is the one further from the ground, and the two states
sit on opposite sides of it rather than one step apart on the same side. The previous
arrangement — selected = `Surface` — was a white row on a `#FAFAFA` ground, which is a five
level difference nobody could see.

- **Disabled** is 45% opacity over the whole control, not a colour. The palette has no
  disabled colour and inventing one would put a value in a view that is not in this
  document. The control keeps its shape and its label; it recedes, it does not change.
- **Text selection** is inverse video: `TextPrimary` ground, `Surface` text, square ends,
  no tint. The caret is `TextPrimary`.
- **Focus** is the 2px ring below. It is drawn *outside* the control, so every control that
  hosts it also turns off clipping and drops the framework's own focus adorner — one
  mechanism for the ring, never two drawn at once.

## Type

One family: **Inter** (variable, OFL, bundled), falling back to the platform UI face. Metrics
use `tnum` + `zero` features; nothing else changes the defaults.

| Role | Size / line | Weight | Tracking | Use |
|---|---|---|---|---|
| `Display` | 40 / 44 | 700 | -0.02em | the page title |
| `Figure` | 34 / 36 | 600 | -0.02em | a percentage |
| `Title` | 28 / 34 | 600 | -0.02em | a heading inside a page |
| `Subhead` | 21 / 28 | 600 | -0.02em | a provider's name on a card |
| `FigureSmall` | 20 / 24 | 600 | -0.01em | a secondary figure, a footer figure |
| `Heading` | 15 / 20 | 600 | 0 | a panel or section heading, a wordmark |
| `Lead` | 14 / 20 | 400 / 500 / 600 | 0 | the line under a title, a metric name, a row name |
| `Body` | 13 / 18 | 400 | 0 | prose |
| `Label` | 12 / 16 | 500 | 0 | a quiet label |
| `Caption` | 11 / 14 | 400 | 0.01em | a timestamp, a token count |
| `Eyebrow` | 11 / 14 | 500 | 0.08em | the line above the page title, in capitals |

Every page title is `Display`, whichever page it is. A window whose pages set their own titles
at their own sizes reads as several applications.

Sentence case everywhere, including section headings and buttons — **except the eyebrow, which
the reference sets in capitals (reference)**. At 11px tracked a full 0.08em open, the capitals
read as a label for the title rather than as shouting, and the eyebrow is the only place they
appear. The view supplies the capitals rather than a style pretending to, because a text
transform would be a lie about the string an accessibility tool reads out.

No italics. One family — **Inter** — with one exception: a monospace face for the chip that
carries a provider's own identifier, because an identifier set in the prose face reads as
prose. Nothing is bundled for it; the stack is the faces a desktop already has with the
platform default behind them, and a chip that falls back to the UI face still reads correctly.

Times read as `2h 14m` and `8:00 PM` in the user's locale format. A figure Altim cannot produce
reads as an em dash — never as a zero, and never as a blank.

## Space, shape, line

- Spacing scale: 2, 4, 6, 8, 12, 16, 20, 24, 32, 48. Nothing in between.
- Radius: `4` for chips and meter ends, `6` for controls, `8` for rows, buttons' focus rings
  and drop downs, `12` for cards, panels and the popup **(reference)**. Nothing else.
- Borders: exactly 1px, `Border`. Separators are 1px `Border` with no margin tricks.
- Shadow: one only, on the popup — `0 8 24 rgba(0,0,0,0.12)` light, `0 8 24 rgba(0,0,0,0.5)` dark.
- Focus: 2px `TextPrimary` ring offset 2px. Always visible, never removed.
- Hit targets: 28px minimum height for rows, 32px for buttons, 40px for a sidebar row.
- Hairlines are snapped to whole device pixels. A 1px rule is one device independent pixel,
  which is 1.25 or 1.5 device pixels at 125% or 150%: unsnapped it is spread over two rows
  at partial coverage and reads as a grey smear rather than a line.

## Components

**Meter.** 8px tall, radius 4, track `Border`, fill `TextPrimary`. A 1px `TextSecondary` tick
marks a configured threshold. Fill animates 180ms ease-out only when the value changes. Above
threshold the fill stays `TextPrimary`; the *label* turns `StatusWarn`. The bar never turns red.
Minimum width 48. A meter reports a level by its width, so one collapsed to nothing by an
auto-sized column reports nothing, silently.

**Metric row.** Label left (`Label`, `TextSecondary`), figure right (`Figure` or `FigureSmall`),
meter beneath spanning full width, optional sub-caption (`Caption`) under the label. Token
counts read `56.2K / 130K tokens` and are shown only when the provider reports them.

**Panel.** The one container in the system: 1px border, radius 12, padding 24, `Surface`
ground, no shadow. A provider card and a dashboard panel are the same shape doing two jobs.
There is no nested variant; sections inside a panel are split by separators and whitespace.

**Provider card.** The panel, used only on Overview, one per provider. Header row: 28px provider
glyph, provider name (`Subhead`), a 16px disclosure that opens the provider's own page, status
dot + word right. Metrics stack below, separated by 16px, each one a `Lead` name with the
`Figure` right aligned and the meter beneath. **Every metric on a card takes the same figure
size (reference)**: elsewhere the first metric on a surface takes `Figure` and the rest
`FigureSmall`, but a card's metrics are a set to be read against one another and two sizes would
say one of them mattered more. The card's token count, when the provider reports one, is a
`Caption` under the metrics.

The footer is pushed to the foot of the card, separated by a 1px rule, and split by a **vertical
hairline** into two cells: "Resets in" over the time remaining, and "Pacing" over the comparison
and the window it was made against. The last refreshed caption sits at the far end of the same
row. The footer is at the foot rather than after the metrics because cards in a row are arranged
at the row's height, and a provider reporting two windows would otherwise put its footer half a
card higher than the one beside it reporting three.

**Card grid.** Equal columns with a 24px gap, dropping from two columns to one when a column
would be narrower than 320. Below that a card's figures start colliding with the labels beside
them, so the grid takes a column away rather than letting every card in the row become
unreadable at once. Every cell in a row is arranged at the row's tallest height.

**Chip.** `SurfaceMuted` ground, radius 4, 4×8 padding, the monospace face at `Caption` size.
It carries a provider's own identifier and nothing else.

**Pill.** 32 tall, radius 8, 1px border, `Surface` ground: a control that reports rather than
opens. The range picker is the same shape with a chevron, because it does open.

**Brand mark.** The Altim mark at 22px, drawn as the template bitmap used as an opacity mask
over `TextPrimary`, so it is ink in Light and ink in Dark from one asset. It appears twice: the
sidebar's brand header and the popup's header.

**Line icons.** A 16px box, stroked 1.5 in the ink of the surface, round joins and caps: home,
clock, gear, chevron right, chevron down, arrow right. Drawn as path data rather than imported
as assets, for the same reason the provider marks are: an imported set would need a second copy
for Dark, would not follow the ink, and would carry licence terms a repository cannot honour by
copying a binary into it. The panel's status icon is the one filled mark: an 18px disc in
`TextPrimary`, or `StatusError` when a provider failed, with a tick or an alert cut through it
in `Surface`.

**Provider marks.** Each provider wears its own vendor's mark, on the same 16px box the line
icons use but filled rather than stroked, in that provider's accent: Claude's is a radial burst
of tapered spokes, Codex's is the knotted hexagonal form. Any other provider keeps a plain
circle. Each is a **single monochrome path** — the accent is what marks identity, so a mark in
its vendor's own colours would repeat what the palette has already said, in colours the palette
does not hold, and would break the rule that provider colour marks identity and nothing else.
Drawn at three sizes from one path: 16 beside a label, 20 in an activity row, 28 where a
provider heads a card.

Every mark fills its 16×16 box on all four sides. A shape stretched `Uniform` is scaled to its
own bounds and pinned to the top left of its slot rather than centred, so a mark whose bounds
are not square hangs to one side of the label it belongs to — which is what a hexagon 13 wide
in a 15 tall box did. One mark is solid and the other hollow, which is what tells them apart at
16px and what balances an `AccentOpenAI` that is very nearly the ink colour itself.

**Trademarks.** The provider marks are the vendors' own and identify the vendors' own products,
which is nominative use. Altim claims no endorsement by, or affiliation with, Anthropic or
OpenAI. Neither mark is restyled or recoloured beyond the single ink it is drawn in, neither is
combined with Altim's own mark, and neither ever stands for Altim.

**Geometry is never held by a view model.** A `Geometry` cannot be built before Avalonia's
rendering platform exists, and the failure lands inside a type initialiser, which the CLR caches
for the life of the process. Paths are held as strings, parsed on first read, and chosen by the
style class a view applies — which happens when the control is already on screen.

**List row.** Activity and history entries are rows, not cards: 1px bottom rule, 12px vertical
padding, hover `SurfaceMuted`.

**Chart (history tape).** Hairline level lines at **0/50/100 per cent (reference)** in `Border`
with `Caption` labels outside the plot, set in tabular figures so evenly spaced rules get evenly
spaced labels. Three rules is the fewest that still says which way is up: the top is the limit,
the bottom is nothing used, the middle is half. One 2px line per provider in `TextPrimary`
(primary) and `TextSecondary` (secondary); 4px dots at samples only when fewer than 32 points;
no fills, no gradients.

Providers are named by a **legend above the plot** — an 8px dot in the line's own weight and the
name beside it — rather than inline at the end of each line, which is the reference's arrangement
and the one that survives two providers finishing the week at the same level. Dates run along
the bottom, evenly spaced, in whole units of the span: six hours over a day, one day over a
week, five days over a month. Where they do not all fit the tape writes every second or every
third one rather than dropping whichever happens to collide, because an axis of evenly spaced
dates is a scale and the same axis with one missing from the middle looks like a defect.

Empty state is a single sentence, wrapped to the control rather than run off its edge, and not
an illustration.

**Sidebar.** 192px, `SurfaceMuted`, 1px right border, owned by the container rather than by the
list inside it — because the header above the list and the footer block below it stand on the
same ground. Three things stacked:

- **Brand header**, 64 tall on a 24px inset: the 22px mark and the wordmark in `Heading`.
- **Navigation**, 40px rows, radius 8, a 16px mark and a `Lead` label 12px apart, the whole row
  inset 8 from the sidebar and its contents 24 from its edge. The selected row is a flat fill
  and nothing else — no accent bar, no bold, no second colour.
- **Footer block**, pinned to the bottom on the same 24px inset: the wordmark in `Body` at the
  heading weight, then the tagline in `Label`.

**Popup panel.** 320px wide, radius 12 **(reference)**, 1px border, `SurfaceMuted`, one shadow.
Five sections split by 1px rules: header, providers, resets, status, action.

- **Header**: the 22px mark and the wordmark in `Heading`, with the gear at the far end.
- **Provider**: a 28px glyph, the name in `Heading` with a disclosure beside it, and one compact
  line — `5h 42% · 7d 68%`, the windows named by their length in `Label` and the figures in the
  ink. **No meters.** A meter reports a level by its width, and at this width three of them
  stacked under one another report a texture. A window with nothing to report is left out of the
  line rather than shown with a dash; a provider with nothing at all to report gets the sentence
  instead of the line.
- **Resets**: one line per provider, carrying the soonest window that provider reports a reset
  instant for, as `5h 12m (Claude Code)`. The rest of its windows are on its own page.
- **Status**: the 18px status icon and one sentence.
- **Action**: "Open Altim →" as a line of text with an arrow, left aligned. Not a filled button:
  the panel already has a primary action — the tray icon it was opened from — and a second
  filled control competing with it is one too many on a surface this small. Opens in under 100ms and closes on deactivate.
The 320 belongs to the *panel*. Its window is wider — 24 either side, 16 above, 32 below — and
transparent, because that is the room the one shadow falls into; anything positioning the
window against the tray icon subtracts that inset. Where the compositor grants no transparency
the shadow goes and the inset paints in the panel's own ground, never as unpainted black.

On the one edge pointing at the tray icon the inset is cut back to the 8 the panel already
stands off it. Reserved room is still window and a window over a tray icon swallows the clicks
meant for it, and on that edge there is nothing to give up: past the panel's near edge the
taskbar the icon sits in covers the shadow. The panel does not move, and the other three edges
keep the whole inset.

## Motion

Only in response to something changing: meter fill 180ms ease-out, popup fade+4px rise 120ms,
theme change crossfade 120ms. No entrance animations, no hover lifts, no spinners longer than
a second — refreshes show a 12px inline `Caption`, not a modal.

## Words

Sentence case, active voice, no exclamation marks, no filler. The greeting is time-based and
plain: "Good morning" / "Good afternoon" / "Good evening". It is the **eyebrow** above the page
title, set in capitals, and the title names the page — "Usage overview" — because that is what
somebody navigating back to it is looking for. The line under the title stays "Here's how your
AI agents are doing."

- Unavailable metric: "Not reported by this provider" — never a zero or a guess.
- Provider error: "Unable to retrieve usage" with a "Retry" button.
- Empty history: "No usage recorded yet. Altim starts collecting when an agent runs."
- Threshold alert: "Session usage reached 80%." Reset alert: "Usage has reset."

- Pacing with too little history: `—`. Reset time not reported: `—`.

**Pacing** is a computation, not decoration: this window's level less the level standing one
window length ago, which is the same point in the previous window. It comes from local history
and it has an answer only when history has one — no sample for that metric, a sample with no
percentage, or a sample so old that the window it measured had already rolled over all read as
`—`. The caption names what it was compared against: "vs. last session", "vs. last week".

Never invent a limit, a percentage, a reset time or a comparison. If the source does not report
it, the UI says so.
