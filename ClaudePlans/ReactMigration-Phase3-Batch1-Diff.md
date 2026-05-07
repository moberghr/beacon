# React Migration — Phase 3 Batch 1 Behavioral Diff

**Date:** 2026-05-07
**Branch:** `feat/react-phase3` (off `feat/react`)
**Spec:** `ClaudePlans/ReactMigration-Phase3.md`

## Goal achieved

Foundation + first reference page (`/app/projects`) ships. The patterns established here are what every other Phase 3 page will copy.

## Core decision shift mid-batch

Originally I started building shadcn/ui primitives + Tailwind base. Mid-batch, surfaced the existing `Beacon-design/` handover folder — a 2783-line CSS design system with matching JSX components (sidebar, page header, dashboard, query detail, task detail, components reference page). Threw out the shadcn approach in favour of using the handover directly.

This means: **no Tailwind utility classes in component code, no shadcn primitives.** The design system is plain CSS with custom properties (OKLCH brand palette, neutrals, dark/light themes) and BEM-ish class names (`.page-header`, `.tbl`, `.empty-state`, `.kpi`, `.card`, `.btn`, `.nav-item`). React components compose those classes.

Tailwind packages are still installed but unused — Phase 2 cleanup can drop them.

## Files added (the reference set every later page copies from)

| File | Purpose |
|---|---|
| `web/src/styles-beacon.css` | The full Beacon design system — vendored from `Beacon-design/styles.css`. 2783 lines, all design tokens + component styles. |
| `web/src/index.css` | Single import of the design system. No Tailwind directives. |
| `web/src/components/Icon.tsx` | Inline SVG icon set, ported from `Beacon-design/icons.jsx` to TS. Exports `Icon.Home`, `Icon.Folder`, `Icon.Refresh`, etc. — 47 icons. |
| `web/src/components/layout/AppShell.tsx` | Two-column grid: sticky sidebar + scrollable main outlet. Matches `.app` class. |
| `web/src/components/layout/Sidebar.tsx` | Ported from `Beacon-design/sidebar.jsx`. 5 nav sections (Overview, Data, Alerts, MCP, Admin). Each item has slug + blazorPath; resolveNavHref picks `/app/<slug>` when migrated, `/beacon/<blazorPath>` otherwise. User chip footer reads `useAuth()`. Active state from `useLocation()`. |
| `web/src/components/layout/PageHeader.tsx` | Compact header — title + sub + actions. Maps to `.page-header`. |
| `web/src/components/data/DataTable.tsx` | Generic typed table built on the design system's `.tbl` grid pattern. Columns + row click + empty state. Plain CSS grid, no TanStack Table yet (Phase 3 Batch 3+ may swap if pagination/sort needs grow). |
| `web/src/components/data/EmptyState.tsx` | Matches `.empty-state` — icon + title + description + action slot. |
| `web/src/lib/format.ts` | `formatDate`, `formatDateTime`, `formatRelativeTime`, `formatNumber`, `formatPercentage`. Locale via `Intl.*`. |
| `web/src/auth/RequireAuth.tsx` | Redirects anonymous users to `/beacon` (Blazor login). Reads `useAuth()`. |
| `web/src/feature-flags.ts` | `MIGRATED_PAGES` constant. Used by Sidebar to decide href targets. **Phase 3 cutover deletes this file.** |
| `web/src/routes/projects/ProjectsListPage.tsx` | First migrated page. Lists projects via `useProjectsQuery()`. DataTable with name+description, sources, repos, last scan (relative), created (relative). Refresh button. Loading + error + empty states. Row click navigates to `/app/projects/{id}`. |
| `web/src/routes/projects/ProjectDetailPage.tsx` | Placeholder route — full detail page lands in Batch 2. |
| `web/src/routes/projects/queries.ts` | `useProjectsQuery()` — TanStack Query hook calling `beaconApi().getProjects()`. |
| `web/src/App.tsx` | Rewritten as React Router root. `<RequireAuth>` wraps everything. Routes lazy-loaded. `<Toaster />` from sonner mounted at root. `basename="/app"` so all routes are relative to that prefix. |
| `ClaudePlans/ReactMigration-Phase3.md` | Phase 3 spec (7 batches over ~9 weeks). |
| `ClaudePlans/ReactMigration-Phase3-Batch1-Diff.md` | This file. |

## Files modified

- `web/package.json` — added `react-router-dom`, `@tanstack/react-table`, `@tremor/react`, `react-hook-form`, `zod`, `@hookform/resolvers`, `sonner`, `class-variance-authority`, `clsx`, `tailwind-merge`, `lucide-react`, `tailwindcss-animate` + several `@radix-ui/*` packages. **Note:** Tailwind, shadcn, and Radix packages are not currently used (the design system handover replaced them). They stay installed for now; Phase 2-style cleanup PR can prune.

## Smoke results (port 5299, anonymous unless noted)

| Path | Result |
|---|---|
| `GET /app` | 200 HTML, 465 B (SPA shell) |
| `GET /app/projects` | 200 HTML, 465 B (SPA, route handled client-side) |
| `GET /app/projects/123` | 200 HTML, 465 B (SPA, dynamic route) |
| `GET /app/assets/index-*.js` | 200 application/javascript, ~268 KB |
| `GET /app/assets/index-*.css` | 200 text/css, ~56 KB |
| `GET /beacon/api/projects` (anon) | 401 application/problem+json |
| `GET /beacon` | 200 Blazor (unchanged) |
| `dotnet test` | 35/35 pass |
| `dotnet build -c Release` | green |
| `npm run build` | green (~84 KB initial JS gzipped, lazy chunks per route) |

## Bundle breakdown

```
dist/index.html                              0.47 kB
dist/assets/index-DPbiaOhN.css              56.27 kB │ gzip: 10.32 kB
dist/assets/ProjectDetailPage-…js            0.57 kB
dist/assets/EmptyState-…js                   0.76 kB
dist/assets/ProjectsListPage-…js            57.26 kB │ gzip:  5.73 kB
dist/assets/index-…js                      267.66 kB │ gzip: 84.21 kB
```

Initial load: ~94 KB gzipped (CSS + main bundle). Per-route chunks ~5 KB each.

## What this batch did NOT do (deferred)

- Project detail page — placeholder only, Batch 2.
- Generated TS client wired up via `client.ts` — done in Phase 1, but `useAuth.ts` still uses hand-written `fetchJson`. The Projects query uses the generated client. Mixed today; Phase 3 Batch 2+ will consolidate.
- TanStack Table integration — used the design system's `.tbl` grid directly. Migrating to TanStack Table when sorting/filtering needs grow.
- Tremor charts — installed but no widgets yet. Land in Batch 2 (Home/dashboard).
- Monaco / Mermaid — packages not yet installed. Land when their pages port (Batch 5 / 6).
- Toast wired into React Query global `onError` — Toaster mounted but not pulled. Lands in Batch 3 with first mutation.
- React Hook Form / Zod — packages installed but no form yet. Land in Batch 3.
- Vitest + RTL + MSW — not set up. Lands in Batch 2 alongside the first non-trivial page test.

## Patterns established (reference for Batch 2+)

1. **Routes live in `routes/<area>/`.** One file per page. Lazy-loaded from `App.tsx`. Sibling `queries.ts` for TanStack Query hooks.
2. **Layout is `.page` container with `.page-header` + content cards/sections.** No Tailwind utility classes.
3. **Tables use `<DataTable>`** with `gridTemplate` prop for column widths. Custom render per column.
4. **Empty / loading / error states** use `<EmptyState>` for non-data-driven, `.muted` text for transient states.
5. **Icons come from `Icon.Xxx`** (the inline set). When a needed icon isn't there, add it to `Icon.tsx` rather than installing lucide.
6. **Auth gate is route-level** — `<RequireAuth>` wraps all `/app/*` routes in `App.tsx`. Pages don't check auth themselves.
7. **Sidebar doesn't update mid-session** — `MIGRATED_PAGES` is build-time. Adding a page to the list requires a deploy.
8. **Format helpers in `lib/format.ts`** — never inline `new Date(...).toLocaleString(...)` in JSX.
9. **API calls via `beaconApi()`** — singleton wrapper from `lib/api/client.ts`. Adds CSRF + cookies.

## Compounded learnings

- The `Beacon-design` folder is the canonical design system reference. **Do NOT introduce Tailwind utility classes or shadcn components.** Add a new design pattern by extending `styles-beacon.css` if it doesn't exist there yet, then adopt the new class.
- React Router `basename="/app"` is the right pattern for SPA mounted at a sub-path. All `<Link>` `to=` props are relative to that.
- Anonymous `GET /beacon/api/auth/me` returns 200 with `isAuthenticated: false` — that's by design (not 401). Other `/beacon/api/*` endpoints return 401 when anonymous. `RequireAuth` distinguishes via `data.isAuthenticated`.
- `lazy(() => import(...))` chunks per route keep first paint fast. Batch 2+ should keep this pattern.

## Phase 3 progress

- [x] Batch 1 — Foundation + Projects reference page
- [ ] Batch 2 — Read-only pages + project detail
- [ ] Batch 3 — Simple CRUD
- [ ] Batch 4 — Medium pages
- [ ] Batch 5 — Heavy 6 (one per slot)
- [ ] Batch 6 — Specialty
- [ ] Batch 7 — Cutover (delete Blazor + Beacon.UI)
