# Todo — React Migration Phase 3 Batch 4 (Medium pages)

**Spec:** `ClaudePlans/ReactMigration-Phase3.md` (Batch 4 section, line 155)
**Predecessor diffs:** Batches 1, 2, 3 (`ReactMigration-Phase3-Batch{1,2,3}-Diff.md`)
**Branch:** `feat/react-phase3` (continue — push to existing draft PR #13)
**Worktree:** `/Users/mirkobudimir/Dev/MiBu/semantico-react`

---

## Critical constraints (carry over)
- **NO Tailwind, NO shadcn primitives.** Beacon-design CSS in `web/src/styles-beacon.css`.
- **NO fake/seed/demo data.** Real data sources only.
- All routes lazy-loaded; one file per page in `routes/<area>/`.
- Tables → `<DataTable>`; icons → `Icon.Xxx`; formatters → `lib/format.ts`.
- API → `beaconApi()` + TanStack Query hooks per area.
- Add slug to `MIGRATED_PAGES` only after end-to-end working.
- **In-app `<Link>` and `navigate()` paths must NOT include `/app/`** — basename adds it.
- After every React-only build, sync `web/dist/` → `wwwroot/app/` (or run `dotnet build`).
- Forms: RHF + Zod (use Zod 4 form: `z.email()` not `z.string().email()`).
- Mutations: `useMutation` invalidating queries on success, toast.success/error from RFC 7807.
- Backend: MediatR `internal sealed class` + primary ctor, `IDbContextFactory<BeaconContext>`, `.Select(new ...)` no `.Include()`, `InvalidOperationException`/`BeaconException`, lambda `x`, LINQ on separate lines, `CancellationToken` everywhere, never edit committed migrations.

## New patterns introduced in Batch 4
- **First Mermaid integration** (project documentation tab). Add `mermaid` package; render in `components/ui/MermaidDiagram.tsx` (lazy chunk so it doesn't bloat initial bundle).
- **Settings forms.** Multi-section forms with collapsible groups. Beacon-design pattern.

---

## Pages to ship

### 4.1 — Project detail tabs content (`/app/projects/:id`)
The shell + tabs already exist (Batch 2). Fill in real content for each tab.

**Blazor:** `ProjectDetails.razor` (multi-tab page, complex).

- [x] **Overview tab** — already populated in 2.1; verify it shows description, datasource summary, repo summary, scan timestamps. If gaps vs Blazor, add.
- [x] **Repositories tab** — list rows: name, URL, branch, last scan, status. Click → external link (or stay-on-page if Blazor doesn't navigate). D3: `GetProjectRepositoriesQuery` if not already wrapped (check existing handler). Add `SetRepositoryTokenDialog` port (RHF + Zod, secret-handling like API keys — never echo token).
- [x] **Documentation tab** — first Mermaid integration. Read `Beacon.UI/Components/Pages/ProjectDetails.razor` for documentation rendering. Sections + Mermaid diagrams. Add `EditDocumentationSectionDialog` port.
- [x] **AI Actors tab** — list AI actors for project. Fetch via existing `AiActorsEndpoints`. Read-only here; create/refine flows are Batch 6 territory (or later).
- [x] Smoke each tab.

### 4.2 — Subscriptions (`/app/subscriptions`)
**Blazor:** `Subscriptions.razor`, `AddSubscription.razor`, `AddSubscriptionDialog.razor` (note: details page is Batch 5b — skip detail here, just list + add).

- [x] D3: handlers `GetSubscriptionsQuery`, `CreateSubscriptionCommand`, `DeleteSubscriptionCommand` (+ any list filters). Endpoints at `/beacon/api/subscriptions`. Register in `BeaconApiEndpoints.cs`. `.WithName(...)` for OpenAPI contract.
- [x] `routes/subscriptions/SubscriptionsListPage.tsx` — DataTable.
- [x] `routes/subscriptions/AddSubscriptionDialog.tsx` — RHF + Zod (multi-step is Batch 5c; here keep single form, defer multi-step to 5c).
- [x] `routes/subscriptions/queries.ts`.
- [x] `MIGRATED_PAGES += 'subscriptions'`. Lazy-route. Smoke.

### 4.3 — Data sources (`/app/data-sources`)
**Blazor:** `DataSources.razor`, `AddDataSourceDialog.razor` (heavy multi-engine — defer to 5d).

- [x] D3: handlers `GetDataSourcesQuery`, `DeleteDataSourceCommand` (+ test connection if Blazor exposes it). Endpoints `/beacon/api/data-sources`.
- [x] `routes/data-sources/DataSourcesListPage.tsx` — list with engine type, status, last test, connection name. Click row → detail (placeholder for Batch 5).
- [x] **AddDataSourceDialog deferred to 5d.** Add a placeholder "+ Add" button that opens a coming-soon dialog OR navigate to the existing Blazor `/beacon/datasources/add` URL via native `<a>`.
- [x] `MIGRATED_PAGES += 'data-sources'`.

### 4.4 — Notifications full (`/app/notifications`)
**Blazor:** `Notifications.razor`, `NotificationDetails.razor`. Batch 2 shipped read-only list; now add actions + detail.

- [x] D3: `MarkNotificationRead`, `DismissNotification`, `GetNotificationDetail` if not present.
- [x] Update `NotificationsPage.tsx` — add row actions (mark read, dismiss). Status filter chip group.
- [x] `routes/notifications/NotificationDetailPage.tsx` — full notification with related subscription, message body, timestamps, actions.
- [x] Lazy-route detail. Smoke.

### 4.5 — Admin settings (`/app/admin-settings`)
**Blazor:** `AdminSettings.razor`. System-wide config (LLM provider, encryption, API key defaults, etc.). Admin-gated.

- [x] D3: `GetAdminSettingsQuery`, `UpdateAdminSettingsCommand`. Endpoints `/beacon/api/admin-settings`. **Authorization:** admin role only at endpoint + at route level.
- [x] `routes/admin-settings/AdminSettingsPage.tsx` — sectioned form (LLM, security, MCP defaults, audit). RHF + Zod. Save per-section or one big submit (mirror Blazor).
- [x] `MIGRATED_PAGES += 'admin-settings'`.

### 4.6 — Settings (`/app/settings`)
**Blazor:** `Settings.razor`. Per-user settings (theme, default project, etc.).

- [x] D3 if needed: `GetUserSettings`, `UpdateUserSettings`.
- [x] `routes/settings/SettingsPage.tsx`. Sections; RHF + Zod.
- [x] `MIGRATED_PAGES += 'settings'`.

---

## Cross-cutting
- [x] `components/ui/MermaidDiagram.tsx` — lazy-loaded mermaid renderer. `mermaid` v10+ npm package. Initialize once with `securityLevel: 'strict'`. Renders diagram from a string prop.
- [x] Test the Mermaid lazy chunk: only loaded when documentation tab is opened.
- [x] `useAuth()` role helper (e.g. `useIsAdmin()`) — used by AdminSettings + Users.
- [x] At least one new Vitest test for one of the mutation flows (Subscriptions or AdminSettings save).

---

## Acceptance gate
- [x] `dotnet build -c Release --property WarningLevel=0` green
- [x] `dotnet test` green; OpenAPI contract still passes for ALL handlers
- [x] `npm run build` green
- [x] `npm test` green
- [x] Manual browser smoke each new page + each project detail tab
- [x] `ClaudePlans/ReactMigration-Phase3-Batch4-Diff.md` written
- [x] `git commit` only — DO NOT push

---

## Out of scope (deferred)
- SubscriptionDetails (heavy) — Batch 5b
- AddSubscriptionDialog multi-step stepper — Batch 5c
- AddDataSourceDialog (heavy multi-engine form) — Batch 5d
- DataSource detail page — Batch 5
- QueryDetails / QueryEditor — Batch 5a / 5f
- McpSettings/Playground/Learning — Batch 6
