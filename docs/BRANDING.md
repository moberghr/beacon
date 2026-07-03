---
nav_exclude: true
---

# Beacon Documentation Branding

This document describes the visual design system for the Beacon documentation site. It mirrors the product design system (`Beacon.UI/web/src/index.css` — brand hue 175 on the oklch scale, emerald on deep neutral surfaces).

## Color Palette

### Primary Colors

| Color | Hex Code | Usage |
|-------|----------|-------|
| Brand Emerald Light | `#34d399` | Glow, accents, gradient start |
| Brand Emerald | `#10b981` | Primary buttons, focus states |
| Brand Teal | `#0e8c72` | Links on white |
| Brand Teal Deep | `#085e4e` | Hover states, gradient end |
| Brand Ink | `#0b1512` | Sidebar (near-black with a green cast) |
| Brand Ink 2 | `#101c18` | Sidebar hover, feedback, headings |

### Semantic Colors

| Color | Hex Code | Usage |
|-------|----------|-------|
| Text Primary | `#232926` | Body text |
| Text Secondary | `#5a6661` | Secondary text, captions |
| Background Primary | `#ffffff` | Main background |
| Background Code | `#f4f7f6` | Code blocks, inline code |
| Border Color | `#e6ecea` | Borders, dividers |
| Table Background | `#fafcfb` | Table rows |

### Gradients

```css
/* Primary gradient (emerald light → emerald → deep teal) */
--gradient-primary: linear-gradient(135deg, #34d399 0%, #10b981 55%, #085e4e 100%);

/* Accent gradient (emerald → deep teal) */
--gradient-accent: linear-gradient(135deg, #10b981 0%, #085e4e 100%);
```

## Logo

The Beacon mark is a lighthouse beacon: an emerald dot emitting light beams, drawn as an SVG.

### Logo Files

- **Wordmark** (mark + "Beacon" text): `assets/images/beacon-logo.svg` — used in the site navigation
- **Mark only**: `assets/images/beacon-mark.svg` — used in the README hero
- Source of truth: `Beacon.UI/web/public/beacon-logo.svg` and `beacon-mark.svg`

### Logo Usage

- Always maintain aspect ratio; minimum height 32px, clear space 8px
- Keep the mark on white or near-black backgrounds; avoid mid-tone backgrounds that swallow the glow
- Do not recolor the beams — the emerald gradient is part of the brand

## Typography

- **Font Family**: System font stack (just-the-docs default)
  - `-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif`
- **Code Font**: Monospace stack
  - `"SF Mono", Monaco, "Cascadia Code", "Roboto Mono", Consolas, monospace`
- Product UI uses Inter — if the docs ever embed custom type, prefer Inter for consistency.

## Accessibility

All colors meet WCAG 2.1 Level AA contrast requirements:
- Text contrast ratio: minimum 4.5:1 (`#0e8c72` links on white pass AA)
- Large text: minimum 3:1
- Focus indicators: emerald (`#10b981`)

## Implementation

The Jekyll theme (just-the-docs) is customized via:
- `_sass/color_schemes/beacon.scss` — Jekyll color scheme (selected by `color_scheme: beacon` in `_config.yml`)

When the product palette changes, update the scss variables and this document together.
