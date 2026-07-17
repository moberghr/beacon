# Beacon Documentation Branding

The Beacon documentation site follows the **Moberg house brand** — the same visual
language as [Warp](https://moberghr.github.io/warp/) and the other Moberg product
docs. It is built with **Astro + Starlight** (see `docs/site/`), not Jekyll.

> The Beacon **product UI** keeps its own emerald design system
> (`src/Beacon.UI/web/src/index.css`). This document covers the **docs + README
> surface only**, which represents the Moberg company and therefore uses the Moberg
> house brand (blue), per the Moberg brand system.

## Accent — Moberg Blue

| Token | Light | Dark | Usage |
|-------|-------|------|-------|
| `--bx-accent` | `#430cda` | `#7657ff` | Links, primary buttons, section numbers, eyebrow dot |
| `--bx-accent-dark` | `#2a068f` | `#430cda` | Hover, gradient end |
| `--bx-accent-light` | `#5a30ee` | `#9481ff` | Beam highlights |
| `--bx-accent-gradient` | `linear-gradient(135deg, #430cda 0%, #2a068f 100%)` | — | Callout / hero gradient |

Do **not** use the old purple `#4E0EFF` (corrected to `#430cda` in 2026-07) and do
**not** use emerald/teal on the docs surface — emerald belongs to the product UI.

## Typography

- **Body / headings:** Helvetica Neue (`--sl-font`) — H1 in Light 300, matching the
  Moberg logotype's thin strokes.
- **Mono / code:** JetBrains Mono (`--sl-font-mono`).

## Logo

The Beacon mark is a lighthouse beacon — a glowing core emitting light beams — with
the beams recoloured to the Moberg blue gradient for the docs.

- **Wordmark** (`docs/site/src/assets/beacon-logo.svg`, also in `public/img/`) — used
  in the site nav (`logo.replacesTitle`) and the landing footer.
- **Mark only** (`beacon-mark.svg`) — used in the README hero.
- **Favicon** (`docs/site/public/favicon.svg`) — the mark on a blue rounded square.

The same blue assets live in `docs/assets/images/` for the GitHub README.

## Where the tokens live

- **`docs/site/src/styles/moberg.css`** — the single source of truth. It overrides
  Starlight's accent (`--sl-color-accent*`) and fonts, and defines the `--bx-*`
  landing tokens. Light is the default; dark applies via Starlight's theme toggle.
- **`docs/site/src/components/landing/Landing.astro`** — the custom landing page
  (hero, numbered sections, terminal panels, screenshot showcase, dark footer),
  mirroring Warp's structure.

## Accessibility

Starlight's accent ramp keeps text/UI contrast at WCAG AA. `#430cda` on white and
`#7657ff` on the dark surface both pass AA for links and buttons.

When the brand changes, update `moberg.css` and this document together.
