# Trademarks

Altim is an independent open-source project. It is not affiliated with, sponsored by, or endorsed
by Anthropic, OpenAI or Google, and none of those companies has reviewed, approved, or supported it.

## Whose marks these are

- **Claude**, **Claude Code** and the Anthropic mark are trademarks of Anthropic PBC.
- **OpenAI**, **Codex**, **ChatGPT** and the OpenAI mark are trademarks of OpenAI, Inc.
- **Google**, **Gemini**, **Gemini CLI** and **Gemini Code Assist** are trademarks of Google LLC.

All other trademarks are the property of their respective owners.

## How Altim uses them

Altim monitors usage of those products, so it has to name them and point at them. The vendors' names
and marks appear for one purpose: to identify which of the user's own tools a number belongs to.
That is nominative use, and Altim keeps inside its limits.

Each of the three providers wears its own vendor's mark: Anthropic's radial burst for Claude
Code, OpenAI's interlocking knot for Codex, Google's four pointed star for Gemini CLI. Any provider
Altim does not recognise wears a neutral circle that is nobody's mark.

- Each mark identifies that vendor's product and nothing else. None of them ever stands for Altim,
  and Altim's own mark is the only one used as Altim's identity.
- The marks are the vendors' own outlines, traced by the [Simple Icons](https://simpleicons.org)
  set from each vendor's published brand asset. Two of the three are in that set today and carry
  no per-icon licence of their own, so that project's CC0 waiver is what applies to them. That
  waiver covers the path data and nothing else; it grants no rights in the trademarks the paths
  depict.
- **The OpenAI mark is not in that set any more, and Altim keeps using it.** It was added in July
  2020 and removed in November 2025, in simple-icons#13944, because nobody obtained OpenAI's
  permission to keep it. Altim's path was traced from it while it was there. Removing it was
  Simple Icons' decision about redistributing a mark as a general-purpose asset for anyone to
  use for anything; it was not a takedown, and it is a different act from what Altim does, which
  is to show a vendor's mark to label that vendor's own product inside a monitor the user is
  running. The basis for all three marks here is nominative use — the same basis as for the
  vendors' names, which no licence grants either — and that basis never rested on the mark being
  in anyone's icon set. If OpenAI asks Altim to stop, Altim will, on the terms in **If you are
  Anthropic, OpenAI or Google** below.
- **Each mark is filled in one flat colour of Altim's choosing, and that colour is not the
  vendor's.** It is a single accent per provider, drawn from Altim's own palette so the mark sits
  legibly on both a light and a dark interface, and it therefore differs between the two. An
  earlier version of this document said the marks were "not recoloured"; that was wrong, and the
  colours are named in `src/Altim.UI/Themes/Tokens.axaml` as `AltimAccentAnthropicBrush`,
  `AltimAccentOpenAIBrush` and `AltimAccentGeminiBrush` so the claim here can be checked against
  the code.
- Beyond that single flat fill they are not restyled, animated, or redrawn. Each is fitted to
  its 16px box by scale and translation alone. The burst and the knot are not square as their
  vendors draw them, so squaring them widens the knot by 1.4 per cent relative to its height and
  the burst by 0.08. **Google's star is already square on its own 24 unit grid**, so both of its
  axes take the same factor and its outline is not distorted at all.
- No mark is combined, locked up, or overlaid with Altim's mark or with another vendor's.
- No more of each mark is used than identification needs: a 16px glyph beside the provider's name.
- Nothing in the product, the documentation, or this repository claims endorsement, partnership,
  certification, or any other relationship.

## This is not covered by the MIT licence

Altim's code is MIT licensed. **That licence covers copyright in Altim's own work and grants no
rights in anyone's trademarks**, including the vendor marks reproduced in
`src/Altim.UI/Formatting/ProviderIdentity.cs`. If you fork Altim, the MIT grant does not give you
permission to use Anthropic's, OpenAI's or Google's marks; your use has to stand on its own
footing, and the further you get from identifying their products the less likely it is to.

The same applies to Altim's own name and mark: the MIT licence covers the code, not the branding. A
fork is welcome, under its own name.

## If you are Anthropic, OpenAI or Google

If you believe any use here oversteps, open an issue or contact the maintainer through
[matthewpg.com](https://matthewpg.com) and it will be changed or removed. The marks are drawn as
paths in one file and one design document, so replacing them with neutral geometry is a small,
self-contained change.
