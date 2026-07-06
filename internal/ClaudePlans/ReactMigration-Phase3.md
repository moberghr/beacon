# React Migration — Phase 3 Spec

**Status:** Draft, awaiting batch-1 approval
**Branch:** `feat/react-phase3` (off `feat/react`)
**Predecessors:** `feat/react` (Phase 0+1, PR #11), `feat/react-phase2` (small cleanups, in flight)

## Goal

Migrate every Blazor page in `Beacon.UI/Components/Pages/` to a React page at `/app/*`, calling the REST endpoints shipped in Phase 1. **Phase 3 ends with Blazor entirely deleted from `Beacon.SampleProject/Program.cs`** and the `Beacon.UI` project removed from the solution.

## Scope by the numbers (from Phase 0 audit)

- **82 .razor pages** across 11 feature areas (44 routable `@page` routes; rest are dialogs/components)
- **49 shared components**
- **~21,300 LOC in Pages/** (most of it MudBlazor markup + `@code` blocks)
- **6 critical-risk pages** (>250 LOC of `@code` each): QueryEditor, CreateMigrationJob, SubscriptionDetails, AddDataSourceDialog, AddSubscriptionDialog, QueryDetails
- **~30 distinct MudBlazor components** in heavy use; 1204 MudText, 524 MudStack, 167 MudDialog, 66 MudDataGrid, 51 MudForm
- **6 feature areas need new MediatR handlers** before page port: Subscriptions, Tasks, Recipients, Notifications, DataSources, DataMigration. Today they call services directly from Blazor pages.
- **28 JavaScript interop calls** — clipboard, mermaid, cron, eval. None blocking.

## Locked architectural decisions

| # | Decision |
|---|---|
| **D1** | Side-by-side. React at `/app/*`, Blazor at `/beacon/*` untouched until end-of-phase wholesale removal. No coexistence routing middleware. |
| **D2** | Migration order: easy (read-only/dashboards) → simple CRUD → medium → heavy 6 → specialty. |
| **D3** | New MediatR handlers added per page batch for the 6 unwrapped feature areas. Wrap existing services with handlers; expose via `/beacon/api/*`; then port the page. |
| **D4** | Shadcn/ui + Tailwind + TanStack Table + Tremor + `@monaco-editor/react` + `mermaid`. |
| **D5** | Hard-coded compiled list of migrated pages — used only by React's sidebar to know which links go to `/app/*` vs `/beacon/*`. |

## Architecture

### URL space during migration

```
/                  → 404 (or redirect to /app, TBD batch 2)
/app/              → React shell (already exists)
/app/projects      → migrated React page
/app/queries       → migrated React page
/beacon/           → Blazor (unmigrated pages render here)
/beacon/api/*      → REST API (used by React)
/beacon/api/hub    → SignalR
/beacon/mcp        → MCP (untouched)
/hangfire          → Hangfire dashboard (untouched)
```

After cutover (final batch): Blazor and `/beacon` deleted. `/app/*` becomes `/`.

### React app structure

```
Beacon.SampleProject/web/src/
├── main.tsx                         # bootstrap (existing)
├── App.tsx                          # router + auth guard (refactor in batch 1)
├── api/
│   ├── generated/beacon-api.ts      # NSwag (existing)
│   └── client.ts                    # auth + CSRF wrapper (existing)
├── auth/
│   ├── useAuth.ts                   # current user (existing)
│   └── RequireAuth.tsx              # route guard (NEW — batch 1)
├── lib/
│   ├── api.ts                       # generic fetch helper (existing)
│   ├── hub.ts                       # SignalR client (existing)
│   └── format.ts                    # date / number / cron formatters (NEW — batch 1)
├── components/
│   ├── ui/                          # shadcn primitives (NEW — batch 1)
│   ├── layout/
│   │   ├── AppShell.tsx             # sidebar + header + outlet (NEW — batch 1)
│   │   ├── Sidebar.tsx              # nav with migrated/blazor split (NEW — batch 1)
│   │   └── Topbar.tsx               # user chip, theme, notifications (NEW — batch 2)
│   └── data/
│       ├── DataTable.tsx            # TanStack Table wrapper (NEW — batch 1)
│       ├── KpiCard.tsx              # Tremor wrapper (NEW — batch 1)
│       └── EmptyState.tsx           # consistent empty-list rendering (NEW — batch 1)
├── routes/                          # one file per page
│   └── ProjectsListPage.tsx         # batch 1 reference port
└── feature-flags.ts                 # hard-coded list of migrated /app/* paths (NEW — batch 1)
```

### Routing (React Router v6)

Single root router in `App.tsx`, all routes wrapped in `<RequireAuth>` except `/login` (TBD whether we even need a login route or just deep-link to Blazor's). Per-page route file in `routes/`. Lazy-load each route via `React.lazy` to keep initial bundle small.

### Data fetching

- TanStack Query default. One `useXxxQuery()` hook per endpoint, in `routes/<page>/queries.ts` or `feature-areas/<area>/api.ts`.
- Hooks call the typed `beaconApi()` client. Don't call `fetch` directly outside `lib/api.ts`.
- Mutations: `useMutation(...)` invalidate matching queries. SignalR updates emit React Query invalidations via `lib/hub.ts`.

### State

- Server state: TanStack Query (per above).
- UI state (open dialogs, form drafts): component state (`useState`) or feature-scoped Zustand store. **No global Redux/Mobx.** Cap Zustand to one store per feature area.
- Auth: React Context + `useAuth()` hook (existing).
- Theme: CSS variable on `<html>`, persisted to localStorage. (Phase 1 already renders dark theme; carry that forward.)

### Forms

- React Hook Form + Zod everywhere. No exceptions. The `MudForm @bind-IsValid` pitfall (memory: `feedback` type) doesn't translate; RHF gives reliable validation state.
- Where the existing handler's request record has constraints (e.g. `[Required]`, length limits), encode them in Zod. The generated TS types from NSwag give field types; Zod adds the constraints.

### Errors

- API errors come back as RFC 7807 `application/problem+json` (Phase 1's middleware). React Query `onError` shows a toast (`sonner` package — add in batch 1).
- 401 → window.location to `/beacon` (Blazor login). Until login is migrated.
- 403 → toast + stay on page.

## Page migration unit ("PMU")

Each page goes through:

1. **Audit** — read the existing Blazor page, list its data dependencies, dialogs, and any non-MediatR service calls. Confirm endpoints exist; if not, add MediatR handlers + endpoints first (D3).
2. **Mockup** — sketch the React shell using shadcn / Tremor / TanStack Table primitives. For 6-critical pages, write a one-pager mockup before coding.
3. **Build** — page file + queries file + any new dialogs (also React) + tests.
4. **Manual smoke** — hit `/app/<page>` in browser. Verify primary flow + at least one error path.
5. **Documented** — sidebar nav link added, page added to `feature-flags.ts` migrated list.
6. **Blazor untouched** — unmigrated pages still work; the equivalent `/beacon/<page>` is left alone until cutover.

## Batches

### Batch 1 — Foundation (~3 days)

**Goal:** A reference page (`/app/projects`) ports cleanly, end-to-end, with all the patterns established. Sidebar, AppShell, DataTable, EmptyState, RequireAuth, format helpers, sonner toast — all built once, copied thereafter.

- [ ] Add npm packages: `react-router-dom`, `@tanstack/react-table`, `@tremor/react`, `react-hook-form`, `zod`, `@hookform/resolvers`, `sonner`, `class-variance-authority`, `clsx`, `tailwind-merge`, `lucide-react`, shadcn primitives via `npx shadcn@latest add ...` (button, card, dialog, input, label, select, table, toast, tabs, badge, avatar, dropdown-menu)
- [ ] `App.tsx` rewrite: React Router setup with `<RequireAuth>` wrapper, lazy-loaded routes, `<AppShell>` layout
- [ ] `components/layout/AppShell.tsx` + `Sidebar.tsx` — sidebar nav matches Blazor's section grouping (Overview, Data, Alerts, MCP, Admin, Settings) per the canonical design system. Migrated links go to `/app/*`, unmigrated links go to `/beacon/*` with a small "Blazor" badge (TBD: maybe just no badge — keep it invisible to users)
- [ ] `feature-flags.ts` exports `MIGRATED_PAGES: ReadonlyArray<string>` — initially `['/app/projects']`. Sidebar reads it.
- [ ] `components/data/DataTable.tsx` — TanStack Table wrapper with sorting, basic pagination, search slot, row click. One implementation, used everywhere.
- [ ] `components/data/EmptyState.tsx` — illustration + title + description + optional action
- [ ] `components/data/KpiCard.tsx` — Tremor `<Card>` + value + delta. For dashboard widgets in batch 3+.
- [ ] `lib/format.ts` — date (`Intl.DateTimeFormat`), relative time, cron readable, byte sizes, percentage
- [ ] `routes/projects/ProjectsListPage.tsx` — first reference page. Lists projects via `GET /beacon/api/projects`. DataTable with name, datasource count, repo count, last scan, created. Click → `/app/projects/{id}` (route registered, page placeholder for batch 2).
- [ ] `routes/projects/queries.ts` — `useProjectsQuery()` hook calling generated client.
- [ ] Manual smoke: visit `/app/projects`, see real data. Verify TanStack Table sort works. Verify auth redirect when logged out.
- [ ] **Acceptance:** `dotnet build` green, `dotnet test` 35/35, `npm run build` green, `/app/projects` renders.

### Batch 2 — Read-only pages + project detail (~4 days)

Pages: ProjectDetail, About, ControlTower, Home (with Tremor widgets), MigrationHistory, QueryVersionHistory, Notifications (read-only initially — actions deferred to batch 4).

- [ ] Add MediatR handlers + endpoints for Notifications listing (`GetNotificationsQuery`) — first new D3 work.
- [ ] One route file per page, copying the Batch-1 reference.
- [ ] Tremor charts on Home page (uses Phase 1's dashboard endpoints).
- [ ] Update sidebar `MIGRATED_PAGES` after each port.

### Batch 3 — Simple CRUD (~5 days)

Pages: Recipients (list + add/update/delete dialogs), Tasks (list + detail), Approvals (list + approve/reject), ApiKeys (list + create + revoke), Users (list + roles).

- [ ] D3 work for Recipients, Tasks (new MediatR handlers wrapping existing services).
- [ ] First mutation flows. RHF + Zod patterns established.
- [ ] First SignalR consumer: `ApprovalUpdated` events trigger React Query invalidation on the approvals list.

### Batch 4 — Medium pages (~7 days)

Pages: Projects detail tabs (overview, documentation, repositories), Subscriptions list + add, DataSources list + add (without QueryEditor — that's heavy), Notifications full (with subscriptions), AdminSettings, Settings.

- [ ] D3 work for Subscriptions, DataSources (new MediatR handlers).
- [ ] First Mermaid integration (project documentation).

### Batch 5 — Heavy pages, one per slot (~3 weeks)

Each heavy page is its own slot. Don't batch them — risk of compounding regressions.

- [ ] **5a — QueryDetails** (275 LOC). Tabs (Overview, Recipients, Subscriptions, Anomaly), version history, change history. Uses Phase 1 endpoints + new ones for steps.
- [ ] **5b — SubscriptionDetails** (294 LOC). Hero, KPIs, Query+Notification settings (in flight in design system memory). Aligns with `beacon-design.css` work.
- [ ] **5c — AddSubscriptionDialog + AddRecipientsDialog** (275+ LOC). Multi-step form. RHF stepper.
- [ ] **5d — AddDataSourceDialog** (282 LOC). Multi-engine connection form with per-engine fields. Heaviest form in the app.
- [ ] **5e — CreateMigrationJob** (365 LOC). Wizard, step-by-step config, validation across steps.
- [ ] **5f — QueryEditor** (395 LOC). Monaco editor, query stepper, parameter dialogs, execution preview. Most complex page.

### Batch 6 — Specialty pages (~5 days)

Pages: McpPlayground, McpLearning, McpSettings, ApiKeys/GenerateApiKeyDialog (if not in batch 3), DataCatalog, DataQuality, DataContractDetails, CreateDataContractDialog.

- [ ] Mermaid for McpLearning relationship diagrams.
- [ ] McpPlayground exercises real-time query execution — likely needs new endpoint or SignalR push.

### Batch 7 — Cutover

- [ ] Verify all 44 routable pages have `/app/*` equivalents.
- [ ] Move `/app` → `/` (root). React serves at the root URL.
- [ ] Delete from `Beacon.SampleProject/Program.cs`: `app.UseBeaconUI()`, `app.UseLoginForm()`, `app.AddBlazorUI("/beacon")`, `app.MapBlazorHub()` (if any), the `/beacon` Map branch.
- [ ] Remove `Beacon.UI` project from `Beacon.sln` and from `Beacon.SampleProject.csproj` references.
- [ ] Move auth middleware (`LoginFormAuthMiddleware`, `BeaconCookieAuthMiddleware`, `BeaconAuthorizationMiddleware`, `LoginEndpoints`, `SetupEndpoints`) from `Beacon.UI` into a new `Beacon.SampleProject.Auth` namespace (or just move to `Beacon.SampleProject/Authentication/`).
- [ ] Remove MudBlazor package reference from `Beacon.SampleProject.csproj`.
- [ ] Update `CLAUDE.md` and `.claude/rules/project-specific.md` — remove §9.2 / §9.3 (MudBlazor pitfalls), update Project Profile.
- [ ] Final regression sweep.

## Out of scope

- No login flow rewrite as a React page in Phase 3. Continue redirecting to `/beacon/login` until cutover, when login moves to `/login` (a small React page) as part of batch 7.
- No new features (every migrated page is a like-for-like port — same data, same actions).
- No accessibility upgrades beyond shadcn defaults. Track separately.
- No mobile/responsive overhaul. Match Blazor's behaviour, no more.
- No CORS, no bearer JWT (still cookie auth, same-origin per D1 stack).

## Testing

- **bUnit goes away** at cutover. New page tests use **Vitest + React Testing Library + MSW** (Mock Service Worker).
- Per-page test minimum: render-without-crash + one happy path. Heavier pages get more.
- Existing **NUnit + WebApplicationFactory harness from Phase 1** stays green throughout. Each new MediatR handler ships with a translation test (§4.6) when its LINQ touches JOIN/GROUP BY/JSON/etc.
- Don't aim for 80% coverage. Aim for tests that fail when a real bug ships.

## Risks

| Risk | Mitigation |
|---|---|
| MudBlazor's `MudDataGrid` features (server-side paging, virtualization, custom cell renderers) don't all map to TanStack Table out of box | Build the patterns we need on the Projects list (Batch 1). Add features lazily as later batches surface them. |
| 6 feature areas (Subscriptions/Tasks/etc.) need new MediatR handlers — that's real backend work, not just UI | Each batch budget includes the handlers. Do NOT defer them. The OpenAPI contract test will catch any "added handler, forgot endpoint" slip. |
| Heavy pages (Batch 5) compound: QueryEditor + Monaco + step form + execution preview + parameter dialogs is 5 patterns interlocking | One page per slot, no parallelization within batch 5. Spec each individually before coding. |
| Cutover (batch 7) hits a route we missed | Pre-cutover sweep: every `@page` directive in `Beacon.UI/` mapped to a React route. Diff stays open until verified. |
| Sidebar / nav drift from canonical design system mid-flight | All sidebar markup matches `beacon-design.css` from day 1 (Batch 1). Any divergence is a code-review-blocking finding. |
| Phase 2 may not have shipped before Phase 3 starts (subagent issues) | Phase 3 doesn't depend on Phase 2 changes. Worst case, Phase 2 lands in parallel. |

## Effort estimate

- Batch 1 (foundation + reference port): ~3 days
- Batch 2 (read-only): ~4 days
- Batch 3 (simple CRUD): ~5 days
- Batch 4 (medium): ~7 days
- Batch 5 (heavy 6): ~3 weeks (one slot per heavy page)
- Batch 6 (specialty): ~5 days
- Batch 7 (cutover): ~3 days

**Total: ~9 weeks** of single-engineer work. Realistically wider given review iterations and uncovered surprises.

## What to ship in this session

Just **Batch 1** today. Approval gate before any other batch.

The reference page determines patterns for the next 60+ pages — so getting it right matters more than getting through it fast.
