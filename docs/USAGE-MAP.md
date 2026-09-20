# The usage map

How the History page's **Daily usage** view is built, and what each square is allowed to
mean.

The History page opens on **Daily usage**: a year of squares, one per local calendar day, a row per
provider, and a combined row summing their tokens when there is more than one. Hover a square or
focus it from the keyboard and it gives the date, the four token components the day was reported in,
the highest percentage of an allowance reached that day, and whether the figure was observed or
backfilled. The combined row lists each provider's peak on its own line and never averages them: two
providers' percentages are measured against different limits, and a number made by adding them would
be a number nobody reported.

A square's shade is a rank rather than an amount. Days that have a figure are sorted and split into
five bands against your own history, so the darkest square means one of your own heaviest days and
not any particular number of tokens. A linear ramp would draw every ordinary day as the palest
square and one cache-heavy day as black.

**Token volume is not a measure of productivity, and the map must not be read as one.** A day spent
re-reading a large codebase moves tens of millions of cache tokens for a handful of edits; a day of
careful work on a hard problem can move a hundredth of that and be worth far more. The map will draw
the first as a dark square and the second as a pale one, because it is a picture of volume and
volume is all it is. The tooltip's split into input, output, cache read and cache write is there so
a heavy day can be seen for what it actually was.

**An unknown day and a day that used nothing are drawn differently.** A hairline outline with no
fill means there is no data for that day: before Altim was installed, further back than the
provider's own history reaches, or a provider that was not installed at the time. The faintest fill
in the ramp means there is data and it says nothing was used. Neither is allowed to stand in for the
other.

**The squares carry the providers' own daily figures.** A live reading is a running total rather
than a day's spending, so Altim asks each provider for its own history instead, at most once a day.
What Altim's own samples contribute is the day's peak percentage, which is a real measurement of how
close to a limit you came. The two are merged field by field, so a day can hold a backfilled token
figure beside a peak observed while Altim was watching, or a token figure and no peak at all because
Altim was not running that day.

How far back a row reaches is whatever a provider still holds, and the two do not match. On the
machine these figures were measured, Codex accounted for 100 days spanning about nine months while
the Claude Code transcript store had four days left on it. Everything older is unknown and is drawn
as unknown. Codex also reports one undifferentiated figure per day rather than a breakdown, so its
days carry that figure as input with the other three components unreported: the tooltip shows a
split only where a provider gave one.
