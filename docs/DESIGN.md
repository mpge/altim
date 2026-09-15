# Altim design system

The contract every view implements. If a value is not here, it does not belong in a view —
add it here first, then reference the token.

## Point of view

Altim is an instrument, not a dashboard. It reports a level and a time, then gets out of
the way. Two ideas carry the identity:

1. **Numbers are the subject.** The percentage is the largest thing on screen; labels stay
   small and quiet. All figures set in tabular numerals so digits do not shift as they tick.
2. **The altitude tape.** The logo is an ascending mark, the name reads as altimeter. Meters
   are flat rails with a hairline limit tick, history is a tape with hairline level lines at
   25/50/75/100. No rounded candy bars, no gradient fills, no shadowed cards stacked on cards.

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
| `StatusOk` | `#15803D` | `#4ADE80` | operational dot, healthy |
| `StatusWarn` | `#B45309` | `#FBBF24` | threshold reached |
| `StatusError` | `#B42318` | `#F87171` | provider unreachable |
| `AccentAnthropic` | `#C05B32` | `#D2764D` | provider glyph and dot only |
| `AccentOpenAI` | `#0A0A0A` | `#F2F2F3` | provider glyph and dot only |

A provider accent never fills a meter, a button, or a background. It marks identity at 16px
and nothing else. Status colour appears as a 6px dot or a single word, never a tinted panel.

## Type

One family: **Inter** (variable, OFL, bundled), falling back to the platform UI face. Metrics
use `tnum` + `zero` features; nothing else changes the defaults.

| Role | Size / line | Weight | Tracking |
|---|---|---|---|
| `Figure` | 34 / 36 | 600 | -0.02em |
| `FigureSmall` | 20 / 24 | 600 | -0.01em |
| `Title` | 28 / 34 | 600 | -0.02em |
| `Heading` | 15 / 20 | 600 | 0 |
| `Body` | 13 / 18 | 400 | 0 |
| `Label` | 12 / 16 | 500 | 0 |
| `Caption` | 11 / 14 | 400 | 0.01em |

Sentence case everywhere, including section headings and buttons. No all-caps eyebrows, no
mixed families, no italics. Times read as `2h 14m` and `8:00 PM` in the user's locale format.

## Space, shape, line

- Spacing scale: 2, 4, 6, 8, 12, 16, 20, 24, 32, 48. Nothing in between.
- Radius: `6` for controls and meters ends, `8` for cards and the popup. Nothing else.
- Borders: exactly 1px, `Border`. Separators are 1px `Border` with no margin tricks.
- Shadow: one only, on the popup — `0 8 24 rgba(0,0,0,0.12)` light, `0 8 24 rgba(0,0,0,0.5)` dark.
- Focus: 2px `TextPrimary` ring offset 2px. Always visible, never removed.
- Hit targets: 28px minimum height for rows, 32px for buttons.

## Components

**Meter.** 6px tall, radius 3, track `Border`, fill `TextPrimary`. A 1px `TextSecondary` tick
marks a configured threshold. Fill animates 180ms ease-out only when the value changes. Above
threshold the fill stays `TextPrimary`; the *label* turns `StatusWarn`. The bar never turns red.

**Metric row.** Label left (`Label`, `TextSecondary`), figure right (`Figure` or `FigureSmall`),
meter beneath spanning full width, optional sub-caption (`Caption`) under the label. Token
counts read `56.2K / 130K tokens` and are shown only when the provider reports them.

**Provider card.** Used only on Overview, one per provider: 1px border, radius 8, padding 20,
no shadow. Header row: 16px provider glyph, provider name (`Heading`), status dot + word right.
Metrics stack below separated by 16px. Footer row separated by a 1px rule: reset time left,
last refreshed right, both `Caption`.

**List row.** Activity and history entries are rows, not cards: 1px bottom rule, 12px vertical
padding, hover `SurfaceMuted`.

**Chart (history tape).** Hairline level lines at 25/50/75/100 in `Border` with `Caption` labels
outside the plot; one 1.5px line per provider in `TextPrimary` (primary) and `TextSecondary`
(secondary); 3px dots at samples only when fewer than 32 points; no fills, no gradients, no
legend box — providers are named inline at the end of their line. Empty state is a single
sentence, not an illustration.

**Sidebar.** 200px, `SurfaceMuted`, 1px right border. Items are 32px rows, radius 6, 13px label,
16px icon; the selected row uses `Surface` (light) / `#1C1C1F` (dark) — no accent bar, no bold.

**Popup panel.** 320px wide, radius 8, 1px border, `SurfaceMuted`, one shadow. Sections split by
1px rules: providers, resets, status, actions. Opens in under 100ms and closes on deactivate.

## Motion

Only in response to something changing: meter fill 180ms ease-out, popup fade+4px rise 120ms,
theme change crossfade 120ms. No entrance animations, no hover lifts, no spinners longer than
a second — refreshes show a 12px inline `Caption`, not a modal.

## Words

Sentence case, active voice, no exclamation marks, no filler. The greeting is time-based and
plain: "Good morning" / "Good afternoon" / "Good evening", followed by "Here's how your AI
agents are doing."

- Unavailable metric: "Not reported by this provider" — never a zero or a guess.
- Provider error: "Unable to retrieve usage" with a "Retry" button.
- Empty history: "No usage recorded yet. Altim starts collecting when an agent runs."
- Threshold alert: "Session usage reached 80%." Reset alert: "Usage has reset."

Never invent a limit, a percentage, or a reset time. If the source does not report it, the UI
says so.
