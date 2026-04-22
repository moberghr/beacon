---
nav_exclude: true
---

# Beacon Documentation Branding

This document describes the visual design system for Beacon documentation.

## Color Palette

The color scheme is derived from the Beacon logo gradient (cyan → blue → purple → magenta).

### Primary Colors

| Color | Hex Code | Usage |
|-------|----------|-------|
| Brand Cyan | `#00d4ff` | Primary accent, links, focus states |
| Brand Light Blue | `#4da6ff` | Secondary accent |
| Brand Blue | `#0066ff` | Primary links, buttons |
| Brand Purple | `#8b5cf6` | Visited links, tertiary accent |
| Brand Magenta | `#e946ff` | Gradient accent |
| Brand Dark Blue | `#0d1b3e` | Headings, dark text |
| Brand Deep Blue | `#1a2b5c` | Secondary headings |

### Semantic Colors

| Color | Hex Code | Usage |
|-------|----------|-------|
| Text Primary | `#24292e` | Body text |
| Text Secondary | `#586069` | Secondary text, captions |
| Background Primary | `#ffffff` | Main background |
| Background Secondary | `#f8f9fb` | Cards, alternating rows |
| Background Code | `#f5f7fa` | Code blocks, inline code |
| Border Color | `#e8ecf1` | Borders, dividers |

### Gradients

```css
/* Primary gradient (cyan → blue → purple) */
--gradient-primary: linear-gradient(135deg, #00d4ff 0%, #0066ff 50%, #8b5cf6 100%);

/* Accent gradient (blue → magenta) */
--gradient-accent: linear-gradient(135deg, #0066ff 0%, #e946ff 100%);

/* Subtle gradient (for backgrounds) */
--gradient-subtle: linear-gradient(135deg, rgba(0, 212, 255, 0.1) 0%, rgba(139, 92, 246, 0.1) 100%);
```

## Logo

The Beacon logo features a stylized "S" made of flowing lines with a gradient from cyan to magenta.

### Logo Files

- **Full color logo**: `assets/images/logo.png` (for light backgrounds)
- **Dimensions**: Recommended height 48-64px for navigation

### Logo Usage

- Always maintain aspect ratio
- Minimum size: 32px height
- Clear space: Minimum 8px around logo
- Do not alter colors or proportions

## Typography

- **Font Family**: System font stack
  - `-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif`
- **Code Font**: Monospace stack
  - `"SF Mono", Monaco, "Cascadia Code", "Roboto Mono", Consolas, monospace`

### Font Sizes

| Element | Size | Weight |
|---------|------|--------|
| H1 | 2.5em | 700 |
| H2 | 2em | 700 |
| H3 | 1.5em | 700 |
| H4 | 1.25em | 700 |
| Body | 16px | 400 |
| Code | 0.9em | 400 |

## Visual Effects

### Shadows

```css
--shadow-sm: 0 1px 3px rgba(13, 27, 62, 0.08);
--shadow-md: 0 4px 12px rgba(13, 27, 62, 0.12);
--shadow-lg: 0 8px 24px rgba(13, 27, 62, 0.16);
--shadow-glow: 0 0 20px rgba(0, 212, 255, 0.3);
```

### Border Radius

- Small elements: 4px
- Medium elements: 6-8px
- Large cards: 8px

### Transitions

- Standard duration: 0.2s - 0.3s
- Easing: `ease` or `ease-in-out`

## Accessibility

All colors meet WCAG 2.1 Level AA contrast requirements:
- Text contrast ratio: minimum 4.5:1
- Large text: minimum 3:1
- Focus indicators: 3px solid cyan with 2px offset

## Dark Mode

The design system includes dark mode support via `prefers-color-scheme`:
- Background: `#0d1117`
- Text: `#e6edf3`
- Code background: `#1a1f28`

## Implementation

The color scheme is implemented in:
- `_sass/color_schemes/beacon.scss` - Jekyll color scheme
- `assets/css/style.css` - Custom CSS overrides

To use in templates:
```css
color: var(--brand-cyan);
background: var(--gradient-primary);
```
