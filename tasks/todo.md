# Todo — React Migration Phase 3

**Spec:** `ClaudePlans/ReactMigration-Phase3.md`
**Branch:** `feat/react-phase3` (off `feat/react`)
**Status:** Pending Batch 1 approval

---

## This session: Batch 1 only — Foundation + reference page (`/app/projects`)

### 1.1 — npm packages
- [ ] Add: `react-router-dom`, `@tanstack/react-table`, `@tremor/react`, `react-hook-form`, `zod`, `@hookform/resolvers`, `sonner`, `class-variance-authority`, `clsx`, `tailwind-merge`, `lucide-react`
- [ ] Initialize shadcn primitives via `npx shadcn@latest init` then `add` for: `button card dialog input label select table toast tabs badge avatar dropdown-menu skeleton separator`
- [ ] Verify `npm run build` still green

### 1.2 — Auth + router scaffold
- [ ] `App.tsx` rewrite: BrowserRouter + Routes + lazy-loaded route files
- [ ] `auth/RequireAuth.tsx` — wraps protected routes; redirects to `/beacon` (Blazor login) when unauthenticated. Reuses existing `useAuth` hook.
- [ ] All routes wrapped in `<RequireAuth>`. No exceptions in batch 1.

### 1.3 — Layout: AppShell + Sidebar
- [ ] `components/layout/AppShell.tsx` — sidebar + main content + (optional) topbar slot. CSS Grid layout matching `beacon-design.css` shell.
- [ ] `components/layout/Sidebar.tsx` — nav grouped: Overview, Data, Alerts, MCP, Admin, Settings. Each nav link reads `feature-flags.ts` `MIGRATED_PAGES` to decide `/app/<path>` vs `/beacon/<path>` href.
- [ ] `feature-flags.ts` — exports `MIGRATED_PAGES: ReadonlyArray<string>` initially `['/app/projects']`.
- [ ] User chip footer in sidebar (existing pattern in canonical design system).

### 1.4 — Shared data primitives
- [ ] `components/data/DataTable.tsx` — TanStack Table wrapper. Generic over `<TData>`. Column defs, sort, filter slot, row click callback, empty state.
- [ ] `components/data/EmptyState.tsx` — icon + title + description + optional action button.
- [ ] `components/data/KpiCard.tsx` — Tremor `<Card>` wrapper with title, value, optional delta.
- [ ] `lib/format.ts` — `formatDate`, `formatRelativeTime`, `formatCron`, `formatBytes`, `formatPercentage`. Locale via `Intl.*`.

### 1.5 — Toast / error handling
- [ ] Mount `<Toaster />` from `sonner` in `App.tsx`.
- [ ] React Query default `onError`: 401 → `window.location.href = '/beacon'`; otherwise `toast.error(...)`.
- [ ] `lib/api.ts` parses RFC 7807 `application/problem+json` error bodies and throws an `ApiError` carrying `title`, `status`.

### 1.6 — Reference page: Projects list
- [ ] `routes/projects/ProjectsListPage.tsx` — DataTable with columns: name, description (truncated), datasource count, repo count, last scan (relative), created (relative). Row click → `/app/projects/{id}` (route registered, page placeholder showing "Detail coming in Batch 2").
- [ ] `routes/projects/queries.ts` — `useProjectsQuery()` calling generated client. Stale time 30s (default).
- [ ] Search/filter slot in DataTable header. Empty-state when no projects.
- [ ] Loading state via `<Skeleton>` (shadcn). Error toast + retry button.
- [ ] Page header: title "Projects", optional "+ New project" button (disabled with tooltip "Coming in Batch 4").

### 1.7 — Verify + test
- [ ] `npm run build` green
- [ ] `dotnet build -c Release` green (Release build runs the React build target)
- [ ] `dotnet test` — 35/35 (no new tests required for Batch 1; visual verification sufficient)
- [ ] Manual smoke:
    - `/app` → redirects/lands on something sensible (likely `/app/projects` per default route)
    - `/app/projects` → renders, lists real projects from DB, sort works, row click navigates to placeholder detail page
    - `/app/projects` while logged out → redirects to `/beacon`
    - Sidebar shows all nav items; "Projects" link goes to `/app/projects`, others go to `/beacon/<path>`
    - `/beacon` (Blazor) still works fully unchanged

### 1.8 — Behavioral diff + commit
- [ ] `ClaudePlans/ReactMigration-Phase3-Batch1-Diff.md` — what shipped, what's deferred, what compounds
- [ ] Update sidebar `MIGRATED_PAGES` to `['/app/projects']`
- [ ] Commit + push to `feat/react-phase3`
- [ ] Open draft PR `feat/react-phase3` → `feat/react`

---

## Approval gate (Phase 2.5)

Before batch 1 starts, confirm:
1. The patterns in `ClaudePlans/ReactMigration-Phase3.md` match what you actually want.
2. Projects list is the right reference page (alternatives: Recipients, Notifications, Home/dashboard widgets — only the first establishes a list-detail pattern).
3. The 9-week / 7-batch breakdown is realistic for your team's calendar.

---

## Out of scope for batch 1 (deferred)

- Project detail page (Batch 2)
- "+ New project" button real action (Batch 4)
- Project documentation tab + Mermaid (Batch 4)
- Any other page (later batches)
- Login flow (still redirects to Blazor `/beacon` until Batch 7 cutover)
