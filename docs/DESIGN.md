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
2. **The altitude tape.** The logo is an ascending mark, the name reads as altimeter. A
   reading measured against a limit is drawn as an instrument reads it: meters are flat rails
   with a graduated scale that tightens toward the ceiling and a hairline index at the limit,
   the dial is that same cross section bent round a swept arc, history is a tape with hairline
   level lines at 0/50/100. No rounded candy bars, no gradient fills, no shadowed cards stacked
   on cards.

   **One scale, every instrument.** The meter and the dial are graduated at the same levels, at
   the same two depths, with the same minimum pitch and the same index. That is what lets a
   figure on a card and the figure on the panel be compared by eye, and it is why the scheme
   lives in one place rather than being restated per control.

Restraint rules: separators and whitespace instead of nesting cards; one accent per surface;
colour carries provider identity, status and **how far a reading has got along its own scale**,
never decoration.

> **Amended 2026-09-18, at the product owner's request.** This document previously read "colour
> carries provider identity and status only, never decoration", and said of both instruments
> that the bar and the dial never turn red. That is no longer true of the dial: its sweep is
> banded, aviation fashion, and changes colour where the reading crosses a boundary. What has
> *not* changed is that colour is never the only thing saying it. Both boundaries stand on a
> mark the face draws anyway, the sweep's length still reports the level on its own, and the
> figure still prints it. See **Dial bands** below. The meter is unchanged and stays
> monochrome.

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

Meter fill is `TextPrimary` in both themes, and the meter's scale and threshold index are
both `TextSecondary` in both themes: one ink for the whole engraving, told apart by
geometry. Status and provider colour:

| Token | Light | Dark | Use |
|---|---|---|---|
| `StatusOk` | `#15803D` | `#4ADE80` | operational dot, healthy, pacing under the last window |
| `StatusWarn` | `#B45309` | `#FBBF24` | threshold reached |
| `StatusError` | `#B42318` | `#F87171` | provider unreachable |
| `AccentAnthropic` | `#C05B32` | `#D2764D` | provider glyph and dot only |
| `AccentOpenAI` | `#0A0A0A` | `#F2F2F3` | provider glyph and dot only |
| `AccentGemini` | `#8857C1` | `#9B72CB` | provider glyph and dot only |
| `DialNormal` | `#1D4ED8` | `#3B82F6` | a dial's sweep below half way |
| `DialCaution` | `#CA6A04` | `#FCD34D` | a dial's sweep from half way to the threshold |
| `DialExceeded` | `#7F1D1D` | `#E04747` | a dial's sweep past the threshold |

One accent per provider, and each is a single ink in that vendor's own hue rather than the
vendor's artwork recoloured. `AccentGemini` is Google's own gradient midpoint: Dark takes it
as it is and Light takes the same hue and saturation down in lightness for a white ground,
which is the treatment `AccentAnthropic` already has. It stands 5.0:1 from `Surface` in Light
and 5.3:1 in Dark, and it is deliberately violet rather than Google's blue, because a blue
glyph would sit on the same panel as `DialNormal` and read as a reading rather than a name.

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
- Radius: `4` for chips, meter ends and a dial legend row's mark, `6` for controls, `8` for rows,
  buttons' focus rings and drop downs, `12` for cards, panels and the popup **(reference)**.
  Nothing else. The legend row takes the `4` rather than the `8` its height would suggest,
  because the rectangle being marked is only 21 tall and an `8` on that is a pill; see **Dial**.
- Borders: exactly 1px, `Border`. Separators are 1px `Border` with no margin tricks.
- Shadow: one only, on the popup — `0 8 24 rgba(0,0,0,0.12)` light, `0 8 24 rgba(0,0,0,0.5)` dark.
- Focus: 2px `TextPrimary` ring offset 2px. Always visible, never removed. The one place
  the 2px *offset* is dropped is the usage map, where a focusable thing is a square from 8px
  up with 2px between it and the next day: a ring held 2px clear would be painted over the
  neighbouring days. There the ring hugs the square and fills the gap that is already
  there — same weight, same colour, drawn where there is room for it. The ring takes the
  *shape* of what it marks: round on the dial, whose face is a circle. A rounded rectangle held
  2px clear of a 144px circle stands 2px off it at the sides and nearly 30 at the corners, which
  reads as a box somebody drew round the dial rather than as the dial being focused.
- Hit targets: 28px minimum height for rows, 32px for buttons, 40px for a sidebar row.
- Hairlines are snapped to whole device pixels. A 1px rule is one device independent pixel,
  which is 1.25 or 1.5 device pixels at 125% or 150%: unsnapped it is spread over two rows
  at partial coverage and reads as a grey smear rather than a line.

## Components

**Meter.** A rail read against a graduated scale: an 8px rail with radius 4, a 2px gap, and a
4px scale under it, 14 in all. Track `Border`, fill `TextPrimary`. Minimum width 48. A meter
reports a level by its width, so one collapsed to nothing by an auto-sized column reports
nothing, silently.

The scale is `TextSecondary` hairlines, and **the interval halves twice on the way up**: every
10 per cent to half way, every 5 from there to 80, every 2.5 over the last fifth. Altim's job is
to say how close a level is to a ceiling, so the tape is coarse where the answer is "nowhere
near" and fine where the difference between two readings is the difference between carrying on
and stopping. The two boundaries are levels the product already treats as meaningful: 50 is the
half way rule the history tape rules at, and 80 is the session threshold Altim ships with.
Neither moves with the configured threshold, because a scale that reshaped itself per metric
would leave the meters in one column measuring against different tapes, and a card's metrics are
a set to be read against one another. **The bar itself stays linear.** It is the graduations
that crowd, never the mapping from a level to a position.

Graduations at 0, 50 and 100 run the full 4px depth and the rest 2px: the same three landmarks
the history tape rules at, so two readings on a page are read against the same marks whichever
control carries them. No two graduations are ever drawn closer than 4px; below that the tape
thins from the fine end and the three landmarks are the last to go. The whole tape is carried
from 160px up, and the narrowest meter the window can produce is 514: a provider card on the
Overview, one column, with the window shrunk to the 820 it refuses to go below.

The threshold is an **index**, not a colour change: a `TextSecondary` mark two hairlines wide
that crosses the rail and carries on to the foot of the scale. It is the only mark that touches
both, and the only one at that weight, so position, extent and weight tell it apart from a
graduation with no help from colour. It is drawn over the fill rather than under it, so it is
still there at 100%. Above the threshold the fill stays `TextPrimary`; the *label* turns
`StatusWarn`. The bar never turns red.

A meter with nothing to report draws the rail as a hairline outline and nothing else: no track,
no fill, no scale, no index. The scale is the apparatus for reading a level and there is no
level, so furnishing the empty rail with everything except a figure would be the one thing this
document does not allow. The control measures 14 tall either way, so a metric arriving does not
reflow the page.

The threshold is named in words by the meter's tip, which is also its accessible name:
`62% used, threshold 80%`, or "Not reported by this provider". The meter is a tab stop exactly
when it has both a level and a threshold, which is the one thing the row around it does not
print, and **focus opens the same tip a pointer opens**. A meter with nothing to add is not a
stop on the way to the next control.

Fill animates 180ms ease-out only when the value changes. Nothing else in the control moves.

**Dial.** The meter's cross section bent round a swept arc, used where one reading is the
subject of a surface rather than one of several in a column. Two surfaces are headed by one: the
tray panel, and every provider card on the Overview. Same 8px rail with fully
round ends, same 2px gap, same 4px engraving, 14 in all — but with the rail innermost and the
engraving on the face's outer edge, so the scale stands on the far side of the rail from the
figure, the way a metric row puts the figure above the rail and the scale below it. Track
`Border`, sweep `TextPrimary`, engraving and index `TextSecondary`: the meter's tokens,
unchanged.

The face is 144 across and the arc is swept 240°, centred on the top. A full circle would put
nothing used and the ceiling at the same place; 240 leaves them symmetrically below the middle
and leaves 120° open at the foot, which is where the words under the figure go. 144 is not a
round number picked for looks: the figure sits *inside* the face, `100%` is 95 wide at `Figure`
with its ink standing 12 either side of the middle, and the clear circle inside the band at 144
is 116 — so at the widest reading the figure stands 9 clear of the rail. At 128 it stands 1,
which reads as the figure touching the instrument.

The scale is the meter's, not a second one: the same levels, **the interval halving twice on the
way up**, 0/50/100 at the full 4px depth and the rest at 2px, and no two marks drawn closer than
4px. The pitch is measured along the arc at the radius the graduations reach furthest in, which
is where two of them stand closest together; measuring at the face's outer edge instead would
keep marks 4px apart out there and closer than that where they actually meet. At 144 across that
arc is 285px, so the whole tape is carried. **The mapping from a level to an angle stays
linear.** Nothing used is at the start of the sweep and the ceiling at its end, and the rail's
round ends fall exactly on those two points — the same arrangement as the meter's rounded rail,
whose leftmost pixel is level 0 rather than level 0 less a corner radius.

The threshold is the same **index**: `TextSecondary`, two hairlines, crossing the rail and
carrying on to the face's edge. It is the only mark that touches both and the only one at that
weight, so position, extent and weight tell it apart from a graduation with no help from colour,
and it is drawn over the sweep rather than under it so it is still there at 100%. Above the
threshold the *name* under the dial turns `StatusWarn`.

**Dial bands.** The sweep is drawn in three, aviation fashion: `DialNormal` up to half way,
`DialCaution` from half way to the configured threshold, `DialExceeded` past it. The colour is a
property of **where the reading has got to**, not a repaint of the whole instrument: a reading of
92 is blue for its first half, amber to 80 and red from there, so the arc says at a glance both
how far it has gone and which range it ended in.

Neither boundary is a new number. Half way is the landmark the scale already rules at and where
its graduation interval first halves; the threshold is the level Altim already notifies at and
where the index already stands. **That is what keeps colour redundant here**: every colour change
lands on a mark the face draws anyway - the full depth graduation at 50 and the two hairline index
at the threshold - so with every hue removed the arc's length, those two marks and the figure
still say the same thing. A threshold below half way collapses the caution band rather than
reordering the three; a reading with no configured threshold has no exceeded band, because there
is nothing for it to have exceeded and Altim does not invent a limit.

**Blue to amber to red, not green to amber to red.** Green and red are the single worst pair for
the two common dichromacies: a protanope or a deuteranope sees them as the same olive. A blue sits
at the far end of the axis both of them keep, so normal is unmistakable from the other two
whatever the viewer's vision; caution and exceeded both fall toward the yellow pole for those two,
so they are separated by lightness instead. Every band stands at least 3:1 from its own ground
under normal vision, protanopia, deuteranopia and tritanopia alike, and the caution and exceeded
bands stand at least 2.5:1 from one another - on top of which the index is drawn exactly where
they meet.

**The meter is not banded.** It reports one of several levels in a column, where three colours per
rail would be a texture rather than a reading, and its fill stays `TextPrimary`. The dial is the
instrument that heads a surface and answers "can I keep working", which is the question a band
answers.

The engraving is radial, so its marks cannot be snapped to the pixel grid the way the meter's
vertical rules are: a mark at 37° lands where it lands. Their weight is still resolved to a whole
number of device pixels and the radii they run between are snapped, so the engraving is one
weight throughout rather than a different grey per mark.

**Several readings, one face.** A dial can carry one sweep per provider, each on its own
concentric ring, outermost first in the order the providers were registered. **Percentages from
different providers are never combined.** 33% of one vendor's weekly allowance and 54% of
another's are proportions of two different, undisclosed limits: their sum, their mean and any
token weighting of them produce an authoritative looking number that measures nothing, and neither
vendor publishes the allowance that would make one meaningful. What the rings share is a *unit* -
how much of that provider's own allowance is gone - which is why they can stand on one face, one
graduation tape and one angular mapping with no arithmetic done to them at all. It is the same
rule that makes the usage map's combined row sum tokens and never percentages.

The order is **registration order, never level order**. Sorting by "nearest its ceiling" would
swap two rings every time the numbers crossed, so a ring somebody had learned to read as one
provider's would quietly become the other's, and the swap would happen exactly when the numbers
were worth watching.

One reading keeps the meter's own 8 rail, to the pixel. Two share a stack of two 5 rings with a 2
between them, three take three 3 rings: the engraving never moves, so the tape is the same on
every face whatever it carries. The limit is the figure standing inside: `100%` is 95 wide at
`Figure` and its ink stands 12 either side of the middle, so what has to clear the innermost ring
is the clear circle's chord at that height. One ring leaves 9 either side, two leave 5, three leave
4, and **a fourth leaves less than nothing**. *A 144 face carries three rings.* A fourth provider
is where either the face or the figure inside it has to give way, and that is the measurement to
look at when one arrives.

**Each ring carries its own provider's head window**, chosen by the same rule a card picks its
dial's window by, so two rings can easily be measuring different windows - one provider's session
against another's week. A ring with no figure draws its own outline and no sweep while the rings
beside it read normally; only when *nothing anywhere* reports does the whole face drop to the
unavailable state below. Each reading brings its own threshold, and one index is drawn per
distinct threshold, from the innermost ring that both reports a level and measures against it. A
ring with nothing reported carries no index: ink laid across an outlined ring would be the one
thing on it that looked like a reading.

**A ring is meaningless without a legend.** Wherever a dial carries more than one reading, every
ring is named beside it, in the rings' own order, with that provider's figure. The mark is a
circle the size of the ring it names - 14, then 10, then 6 - so "the big circle" and "the outer
ring" are the same thing with nobody being told the convention, and it is a size rather than a
colour because two rings are often in the same band and because a size survives the colour being
taken away. A provider that reported nothing keeps its row and shows an em dash: a ring missing
from the legend would be a provider missing from the panel. A face carrying **one** reading has
no legend: the words under the figure have already named it, and with no second arc there is
nothing for a key to tell it apart from.

**The legend and the face are joined, not merely ordered.** Pointing at a legend row traces that
row's band on the face; reading a band on the face traces that band's row. It is one mark in two
places - the same hairline, in the same ink, appearing and going in the same frame - because two
identical marks read as one thing and two different ones have to be learned. There is exactly one
piece of state behind it, owned by the dial: a pointer on a band wins, a legend row being pointed
at or arrived on comes next, and the ring the keyboard is on is what is left, so letting go of a
row falls back to the ring rather than to nothing. The row's circle is still not coloured, for
the reason it never was.

The legend rows are **focus stops**. Pointing at one marks a band, so the keyboard has to reach
them too; the ring is the design system's, drawn outside the row, and it covers the row's own
hairline rather than standing beside it. Both take the system's smallest radius, 4, rather than
the 8 a row usually takes: a legend line is 16 tall, so the rectangle being marked is 21, and at 8
that is a pill whose ends sweep back across the 14 wide circle the row draws its own mark as. The
rows are not brought up to the 28 a hit target takes, because nothing here is actuated: the whole
cost of missing one is a mark that does not appear.

A dial with nothing to report draws the rail as a hairline outline and nothing else: no track, no
sweep, no engraving, no index, and the figure inside it is an em dash. **There is never a sweep
sitting at the bottom of the scale**, which is the one picture that would read as a reported
zero. Nothing used draws the rail filled with its track and no sweep either — a sweep of no
length is not a reading of nothing — so the two states are told apart by the rail behind them,
which is exactly how the meter tells them apart.

The reading is named in words by the dial's accessible name, and the words are the meter's:
`62% used, threshold 80%`, or "Not reported by this provider". A view that names the dial has that
name read first - "Session (Claude Code), 62% used, threshold 80%" - which matters more here than
on the meter, because the dial shows one window out of several and the name is what says which.
**Every ring is named, not only the one the figure belongs to**: a client handed the highest
reading alone would be handed a face with fewer sweeps on it than it has.

**The tip says more than the name, and that is deliberate.** Pointing at a ring, or arriving on it
with the keyboard, gives that ring's reading, when its window rolls over, and **what the bands mean
in words**: which band the reading is in, where caution begins and where the threshold stands.
Colour is being asked to carry meaning, and a colour code nobody can read out is a code the reader
has to learn. Those words are not in the accessible name, because the audience that receives no
colour needs no colour key and would hear it on every announcement; the level and the threshold
already say everything the bands mark.

The ring being read is marked on the face by a hairline traced round its own band, in the
engraving's ink. It is static - nothing on this control moves - and it is the same mark whether a
pointer, the keyboard or a legend row asked for it, so **focus reveals exactly what hover
reveals**. The arrows
step between rings, outward and inward, stopping at the ends rather than wrapping; a dial carrying
one reading leaves the arrow keys alone, because it stands inside the Overview's scrolling area
and one that swallowed them would stop the page scrolling to say nothing. The dial is a tab stop
when it has a threshold measured against a level, and always when it carries more than one ring:
which ring belongs to whom is drawn and nowhere else said.

**The dial does not animate**, on either surface. The meter's fill travels 180ms because it
lives on a window somebody already has open. The dial's first surface is the tray panel, which is
laid out once at start-up, hidden rather than closed, and goes on taking readings while nobody is
looking at it: a transition there would rebuild a path per frame for a panel nobody can see, and
the panel's whole budget is that opening it is a show rather than a build. Its two paths are
built once and rebuilt only when the face's size or the level moves. The Overview's cards do
stand on a window somebody has open, and the dial stays still there too, because one instrument
that moves on one surface and not on another is two instruments.

**The figure standing in the face is not announced twice.** It is the dial's own reading, which
the dial's peer already reads out in words, so a view that stands a figure inside the face takes
that text out of the content view. Without that a screen reader says the percentage once as the
dial and again as the text standing in it, which is the one duplication a drawn control invites
and the one a view has to decline.

**Metric row.** Label left (`Label`, `TextSecondary`), figure right (`Figure` or `FigureSmall`),
meter beneath spanning full width, optional sub-caption (`Caption`) under the label. Token
counts read `56.2K / 130K tokens` and are shown only when the provider reports them.

**Panel.** The one container in the system: 1px border, radius 12, padding 24, `Surface`
ground, no shadow. A provider card and a dashboard panel are the same shape doing two jobs.
There is no nested variant; sections inside a panel are split by separators and whitespace.

**Provider card.** The panel, used only on Overview, one per provider. Header row: 28px provider
glyph, provider name (`Subhead`), a 16px disclosure that opens the provider's own page, status
dot + word right.

**The card is headed by a dial**, carrying the window nearest its ceiling out of the ones *this*
provider reports, with the rest of its windows as rows beneath it and a 1px rule between the two
— the rule the tray panel draws under its own reading, doing the same job. The window on the
dial is not also a row underneath it: a card that drew one window twice would have a reader
counting one more window than the provider reports.

The face is the panel's 144, unchanged, and so is the scale on it. **That is the whole answer to
two cards side by side.** A gauge suits one reading against a target, and several gauges in a row
is the arrangement usually advised against, because gauges do not compare — but what makes two
gauges incomparable is two scales, two sizes or two positions. These are one control at one size
on one shared scale, and the card grid arranges every cell in a row at the row's height, so the
two faces stand at the same height in both cards and two sweeps end at angles that can be read
against one another without reading either figure. A face sized per card would give that up,
which is why there is no per-card face size: an instrument that can be resized is an instrument
that can stop comparing.

What the shared scale cannot say is *which* window each dial is carrying, and two providers can
easily have different ones nearest their ceilings — Claude Code's session against Codex's
week. So the window is named, in `Lead` at the medium weight, standing in the 120 degrees the
sweep leaves open at the foot of the face. It goes inside the face rather than under it because
the arc's last ink is a quarter of the face above the face's own bottom edge: a name set under
the whole control stands 44 below the dial it names and 16 above the first row beneath it, which
reads as a heading for the rows. The provider is not named there — the card's header has said
it four lines up — but it is in the dial's accessible name, which is what somebody arriving at
the dial alone hears: `Session (Claude Code), 62% used, threshold 80%`.

The dial's own reset is not printed under it the way the panel prints it. A card reports one
reset, the soonest window's, in the footer, and on almost every reading that is the window on the
dial.

A provider whose reading failed carries no metric, so its card has no dial and no rule where one
would be: the sentence and the retry stand there instead. A head window the provider reports no
figure for keeps its dial and draws the unavailable face — an outline, no sweep — with an
em dash inside it, because unknown is not zero and a sweep sitting at the bottom of the scale is
the one picture that would read as a reported nothing.

Metrics stack below the rule, separated by 16px, each one a `Lead` name with the `Figure` right
aligned and the meter beneath. **Every metric on a card takes the same figure size (reference)**,
and that includes the figure inside the dial: elsewhere the first metric on a surface takes
`Figure` and the rest `FigureSmall`, but a card's figures are a set to be read against one
another and two sizes would say one of them mattered more. The dial is what marks the head
window, not a bigger number — which is also what keeps the face at 144, because `100%` set in
`Figure` is the measurement the face was sized against. The card's token count, when the provider
reports one, is a `Caption` under the metrics.

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
icons use but filled rather than stroked, in that provider's accent: Claude's is Anthropic's
radial burst, whose tapered spokes run out from a solid centre at uneven lengths, widths and
spacings; Codex's is OpenAI's interlocking knot, six braided strands around an open hexagon;
Gemini's is Google's four pointed star, whose arms taper to a point along concave flanks.
Any other provider keeps a plain circle. Each is a **single monochrome path** — the accent is
what marks identity, so a mark in its vendor's own colours would repeat what the palette has
already said, in colours the palette does not hold, and would break the rule that provider
colour marks identity and nothing else. Drawn at three sizes from one path: 16 beside a label,
20 in an activity row, 28 where a provider heads a card.

**The path data is the vendors' own outlines, not a drawing of them.** All three come from the
[Simple Icons](https://simpleicons.org) set, which is CC0 and traces each mark from the
vendor's own published brand asset. Each was moved onto the 16px box by scale and translation
alone, so the outline is still the vendor's: the burst is one closed figure of 158 segments,
the knot is a silhouette with seven counters cut out of it, the star is one closed figure of
quadratic flanks and three circular arcs. A mark redrawn from memory is a different mark that
resembles one, and resembling one is the failure mode that gets shipped — it passes every test
a path can be held to and still reads wrong to anyone who knows the mark. The fitting is done
by a path transformer rather than by hand, and it has to resolve a smooth quadratic's implied
control point — the reflection of the one before it — or the flanks of the star come out
distorted with every endpoint, every bound and every quadrant still correct.

Every mark fills its 16×16 box on all four sides. A shape stretched `Uniform` is scaled to its
own bounds and pinned to the top left of its slot rather than centred, so a mark whose bounds
are not square hangs to one side of the label it belongs to — which is what a hexagon 13 wide
in a 15 tall box did. Two of the three are not square on their own grid — the burst is 0.08 per
cent narrower than it is tall, the knot 1.4 per cent — so each axis is scaled to the box on its
own. Google's star is already square there, so both its axes take the same factor and its
outline is not distorted at all. The knot is open at its centre where the other two are solid,
which is what tells it from them at 16px and what balances an `AccentOpenAI` that is very
nearly the ink colour itself; the burst and the star are told apart by everything else, the
burst covering its box and the star running out along the two axes and tapering.

All three paths declare `F1`, the non-zero fill rule, because that is the rule SVG applies when
a file names none and therefore the rule the vendors' own files are drawn under. On this artwork
it changes nothing — no two subpaths overlap, the knot's counters are nested rather than lapped,
and the star's outline never crosses itself — so even-odd draws the same picture, which
`ProviderGlyphTests` checks rather than assumes. It is declared anyway: re-tracing any of them
from a newer vendor asset whose subpaths do lap would otherwise hole those laps out with no
error and no failing parse.

**Trademarks.** The provider marks are the vendors' own and identify the vendors' own products,
which is nominative use. Altim claims no endorsement by, or affiliation with, Anthropic, OpenAI
or Google. No mark is restyled or recoloured beyond the single ink it is drawn in, none is
combined with Altim's own mark, and none ever stands for Altim. Simple Icons' CC0 waiver
covers the traced path data and nothing else: the vendors' trademark rights in the marks those
paths depict are untouched by it. The full notice, including what the MIT licence does not
grant a fork, is in [TRADEMARKS.md](../TRADEMARKS.md).

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
Six sections split by 1px rules: header, reading, providers, resets, status, action.

**Only the providers this machine actually has.** Altim registers every provider it knows how to
read, so a machine with one agent installed listed three, two of them saying nothing. A provider
whose status is settled as *not detected* is left out of the rows, the rings, the legend and the
resets alike — a row dropped while its ring stayed on the face would be worse than the clutter it
removed. **A provider whose last reading failed is never left out**: it is installed, Altim could
not read it, and hiding it would turn a fault into a silence. Nor is one that has not been probed
yet, which would otherwise appear a moment later and push the panel about under the reader's
pointer; until it answers it reads as any provider with no figures does. This is a consequence of
what is installed, not a preference, and there is no setting for it.

The same rule gives Overview its cards. Three surfaces are deliberately outside it. The **status
line** speaks for every provider, because one sentence saying "Gemini CLI not detected" is what
explains a short list, and because it is what says "No providers detected" when the list is empty.
**History** draws every provider, because one uninstalled today still used something last week and
dropping its line would erase the past rather than tidy the present. **Settings** lists every
provider with its own status sentence, because that page is the inventory and is where a reader
goes to find out why something is missing. The **sidebar** keeps a page for every provider too:
navigation is a table of contents rather than a report, and that page is where the sentence and
the retry live for somebody who has just installed an agent.

With none of them installed the provider section carries its one sentence, "No providers
detected", and the status line arrives at the same words over the same set.

- **Header**: the 22px mark and the wordmark in `Heading`, with the gear at the far end.
- **Reading**: the panel's hero. **One dial, centred, carrying one sweep per provider** on its
  own concentric ring, with the figure in `Figure` inside its face, 8 under it the window that
  figure belongs to in `Lead` and when it resets in `Caption`, and 8 under that the legend naming
  every ring. The figure is the reading nearest its ceiling out of the rings, because that is the
  one that decides whether work can continue; the legend is what turns every other ring back into
  a provider, and pointing at either end of a pairing marks the other. The window on the dial is
  therefore named twice on this panel, under the face and again in the legend, and that is
  deliberate: they answer two questions - "what is the big number" and "which arc is whose" - and
  dropping either leaves one of them unanswered. Nothing is combined: see **Dial bands** and the rings above. The name always carries its provider in parentheses —
  `Session (Claude Code)`, the form the resets section already uses — because the panel reports
  several windows and a figure that does not say which one it measures is a figure nobody can
  act on. The reset reads `Resets in 2h 14m`, or `Resets in —` when the provider reports no
  instant, and it is measured against the clock rather than fixed when the reading landed: a
  countdown that stands still while the time it describes runs out is a figure that stops being
  true without anything having changed.

  **The name wraps.** A window's name can come straight out of a vendor's rate-limit payload —
  `gpt-5-codex-high priority 5 hour (Codex)` already spans 273 of the 288 the section has — so
  centred and unwrapped it runs out of *both* sides of a panel with nothing to clip it. It wraps
  rather than trims, for the reason the compact line does: trimming takes the provider off the
  end, and a window named without its provider is exactly the figure nobody can act on. The
  second line arrives only in the case that used to overflow, so the panel's height is unchanged
  at the lengths names actually are.

  **The window is the highest level any provider reports**: the one nearest its ceiling. The
  panel already prints every window's figure on its provider's own line, so the dial is not
  there to add a number — it is there to say which of those numbers decides whether you can keep
  working, and it is the only place the panel shows the configured threshold at all. It is the
  same rule an Overview card picks its own head window by, run over every provider's windows
  rather than over one provider's: what differs between the two surfaces is the set, not the
  rule, and a tie break written out twice is a tie break that drifts. Ranking by
  the raw level rather than by how near each window is to its own threshold is deliberate: every
  window is drawn against one shared scale, which is the whole reason two readings can be
  compared by eye, and ranking them by a ratio to a per-window alert level would order them by
  something nobody can see on that scale. A tie goes to the window that rolls over first, then
  to the order the providers were registered in, so the same readings always pick the same
  window. A window with no figure never displaces one that has one; when nothing anywhere
  reports a figure the dial still names a window and draws its unavailable face with an em dash,
  rather than leaving the panel headed by nothing. A panel with no window at all — every
  provider unreachable, or none configured — has no reading section and no rule where one would
  be, because there would be no window to name and an unnamed dial is furniture.
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

**The panel never loses its top edge.** Its height follows its content and its content follows
the number of providers: measured, 529 for one, 659 for two, 765 for three. A 1366x768 screen
has 728 to give and a 1920x1080 screen at 175% has 576, so on those the panel cannot be placed
whole and something has to go. The order is fixed: the bottom first, then the 8px margin, then
the tray icon, and the top last of all, which is to say never. The top carries the wordmark,
the settings gear and the head of the dial, and a panel that opens with those above the screen
has lost what says what it is and how to leave it, with no way to scroll back to them.

So the window takes a ceiling, the working area less the panel's margin either side, and the
panel scrolls inside it: an 8px lane on the right, the same one every other scrolling surface
here uses, appearing only where the panel does not fit. Where it fits there is no scrollbar and
nothing about the panel changes.

## Motion

Only in response to something changing, and only where the machine has not asked for stillness.
No entrance animations, no hover lifts, no spinners longer than a second — refreshes show a 12px
inline `Caption`, not a modal.

**The meter fill is the only animation in Altim**: 180ms ease-out, travelling from the level that
was reported to the level that is. It does not run on the first value, on becoming unavailable or
on coming back from unavailable, because none of those is a level changing.

Two further transitions were specified here and never built: a popup fade with a 4px rise, and a
theme crossfade, both at 120ms. The panel is shown and the palette is swapped in one frame each,
and always have been. The specification is withdrawn rather than left standing over something
nobody wrote — it described an interface busier than the one that exists, and it sent the first
person to go looking for motion to make accessible hunting for two animations that were not there.
If either is ever built it goes through the same gate as the fill, and a source-shape test fails
the build if it does not.

The dial is the one instrument that does not move, and the reason is above: its surface goes on
taking readings while it is hidden, so a transition there would animate a picture nobody can see
and rebuild a path for every frame of it. That includes its hover treatment: the mark round the
ring being read, and the matching one round the legend row that names it, appear and disappear in
one frame, which is why there is nothing here for the reduce-motion setting to suppress.

### Reduced motion

Every desktop carries an accessibility setting that asks applications to stop animating —
"Animation effects" on Windows, "Reduce motion" on macOS, `enable-animations` on a GNOME desktop.
People switch it on because motion makes them unwell. Altim reads it, obeys it, reacts while it is
running when the platform reports it changing, and **offers no setting of its own**: the operating
system owns this answer, and a second copy of it in Altim's settings could only ever disagree with
the first, with nothing on screen to say which one was winning.

The reading has three states, not two, because "Altim could not find out" is a real answer:
Windows can refuse `SPI_GETCLIENTAREAANIMATION`, a Mac can be unreachable through its Objective-C
runtime, and a Linux session may run no portal or one that does not carry GNOME's namespace.

| The platform says | What Altim does |
|---|---|
| the user asked for reduced motion | nothing animates |
| the user did not | the meter fill animates |
| nothing Altim could read | nothing animates |

**The unknown falls to stillness, and that is a decision rather than a default.** It is also the
one place in Altim where an unknown is rendered as a value, because a surface either animates or
it does not and there is no third picture to draw. The tie is broken on the size of the two
mistakes: animating for somebody who asked for stillness and whose machine could not be questioned
can make them ill, and withholding a 180ms ease from somebody who never asked cannot. The unknown
itself is not thrown away — it survives in the platform service and in the start-up log, and only
the one call that has to produce a yes or a no collapses it.

**Reduced never means less.** The meter still reaches its new level; it is drawn there in the next
frame instead of travelling. A fill already in the air when the setting is switched on is cut
short at the level it was travelling to, so the relief arrives on the animation that is on screen
and not only on the next one. Nothing disappears, nothing is delayed, and no figure is withheld.

## Words

Sentence case, active voice, no exclamation marks, no filler. The greeting is time-based and
plain: "Good morning" / "Good afternoon" / "Good evening". It is the **eyebrow** above the page
title, set in capitals, and the title names the page — "Usage overview" — because that is what
somebody navigating back to it is looking for. The line under the title stays "Here's how your
AI agents are doing."

- Unavailable metric: "Not reported by this provider" — never a zero or a guess.
- Nothing read yet: "Waiting for the first reading." **This is a fourth state, not the first
  one.** "Not reported by this provider" says the provider was asked and reported nothing, and
  a surface built before anything has been read has not asked. The first read spawns a vendor
  command line and has taken seven seconds on the verification machine, so it is seconds of a
  wrong sentence rather than one frame of one. It is the same sentence the provider page's
  integration section shows for that state, held as one constant so the two cannot drift.
- Provider error: "Unable to retrieve usage" with a "Retry" button. **The figures go with it** —
  the metric rows, the dial, the token counts and the session list are all dropped, because a
  reading that failed says nothing about any of them and a number left standing beside the
  sentence reads as a current one.
- Empty history: "No usage recorded yet. Altim starts collecting when an agent runs."
- No provider installed: "No providers detected" — the provider section's sentence and, over the
  same set, the status line's.
- Settings that could not be read: the page says so, says the values under it are the defaults
  rather than the reader's own, and disables everything that writes one. A read that failed is
  not a write that failed and does not borrow its sentence. **Nothing is written until a read
  has succeeded**: the page is built on the defaults, so one save over an unread record
  replaces the stored settings with them.
- Threshold alert: "Session usage reached 80%." Reset alert: "Usage has reset."

- Pacing with too little history: `—`. Reset time not reported: `—`.

**Pacing** is a computation, not decoration: this window's level less the level standing one
window length ago, which is the same point in the previous window. It comes from local history
and it has an answer only when history has one — no sample for that metric, a sample with no
percentage, or a sample so old that the window it measured had already rolled over all read as
`—`. The caption names what it was compared against: "vs. last session", "vs. last week".

Never invent a limit, a percentage, a reset time or a comparison. If the source does not report
it, the UI says so.
