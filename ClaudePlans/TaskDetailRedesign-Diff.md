# Task Detail Redesign — Diff

Phase 3 follow-up to Batch 5a. Replaces the placeholder Task Detail page with the
"signal" multi-card layout from `Beacon-design/task-detail.jsx`.

Branch: `feat/react-phase3` (continues from 5a backend).

---

## Backend (D3)

### New MediatR slices (one file, internal sealed + primary ctor + records)

| File | Wraps | Endpoint |
|---|---|---|
| `Beacon.Core/Handlers/Tasks/GetTaskExecutionsHandler.cs` | `taskService.GetTaskExecutionHistory` | `GET /tasks/{id}/executions` |
| `Beacon.Core/Handlers/Tasks/GetTaskRelatedHandler.cs` | `taskService.GetRelatedTasks` | `GET /tasks/{id}/related` |
| `Beacon.Core/Handlers/Tasks/GetTaskResultHistoryHandler.cs` | `taskService.GetResultCountHistory` | `GET /tasks/{id}/result-history` |
| `Beacon.Core/Handlers/Tasks/GetTaskCommentsHandler.cs` | `taskService.GetTaskComments` | `GET /tasks/{id}/comments` |
| `Beacon.Core/Handlers/Tasks/AddTaskCommentHandler.cs` | `taskService.AddTaskComment` (with `IBeaconUserContext`) | `POST /tasks/{id}/comments` |

All return shapes are dedicated records (`TaskExecutionsResult`, `TaskRelatedResult`,
`TaskResultHistoryResult`, `TaskCommentsResult`, `AddTaskCommentResult`) — service DTOs
are not leaked through MediatR. `AddTaskCommentHandler` throws `InvalidOperationException`
on whitespace content.

### Enriched existing handler

`Beacon.Core/Handlers/Tasks/GetTaskDetailHandler.cs` — added two fields to `TaskDetailResult`:
- `LastExecutionAt: DateTime?` — most recent `QueryExecutionHistory` row for the parent subscription.
- `CronExpression: string?` — parent subscription's `CronExpression`.

`Beacon.Core/DTOs/TaskDetailsData.cs` — added matching properties.

`Beacon.Core/Services/TaskService.cs::GetTaskDetails` — extended the existing single
`.Select(new ...)` projection to populate both fields. No new round-trip; the LastExecutionAt
sub-query is a correlated subquery that EF translates to a `LATERAL` lookup on Postgres.

### Endpoint wiring

`Beacon.SampleProject/Endpoints/TasksEndpoints.cs` — appended five endpoints with names
matching the OpenAPI contract heuristic (strip `Command`/`Query`):

- `GetTaskExecutions`, `GetTaskRelated`, `GetTaskResultHistory`, `GetTaskComments`, `AddTaskComment`.

Auth is inherited from the parent group (`MapBeaconApi` already calls `.RequireAuthorization(AuthPolicyName)`).

---

## Frontend

### Page replaced

`Beacon.SampleProject/web/src/routes/tasks/TaskDetailPage.tsx` — fully rewritten. Layout:

1. `TaskHero` — page-hero variant: breadcrumb eyebrow + status pill, verb-emphasis title
   ("Investigating *subscription*"), age + SLA-remaining sub, action row (Assign / Snooze / Resolve).
2. `SlaBanner` — open/resolved banner with `.sla-meter` fill (% of SLA elapsed). SLA assumed
   24h (no backend SLA field today). Warns at >=80%, crit at >=100%.
3. `TaskKpiGrid` — Latest result count / Executions / Notifications / Task age (4 KPIs).
4. `q-layout` two-column: main column (info card, result chart, tabs card, investigation log)
   + right rail.
5. `TaskInfoCard` — KV grid using the existing `.kv` / `.kv__row` / `.kv__label` / `.kv__value`
   classes.
6. `TaskResultChart` — inline SVG chart, ported from the design verbatim and parameterized.
   Renders an empty-state if fewer than 2 samples.
7. `TaskTabsCard` — Activity / Executions / Notifications / Related.
   - **Activity**: synthesized timeline merging task creation, optional resolution,
     each execution (`tone=ok` if rowCount > 0 else `neutral`), and each comment. Sorted newest-first.
   - **Executions**: HTML `<table>` inside `.data-tbl` (mirrors design). Status pill colored
     by row count (any rows → ok, zero → neutral).
   - **Notifications**: empty-state — copy adapts to `notificationCount`.
   - **Related**: HTML `<table>` linking to other tasks for the same query.
8. `InvestigationLogCard` — composer (textarea + counter, max 2000) + comments list.
   Pure `useState` + `useMutation` (no RHF, single textarea). Toast on success/failure.
9. `RightRail` — Suggested next steps (heuristic, gated on real conditions:
   "Wire a recipient" only if `notificationCount === 0`; "Mark as expected" only if
   `relatedResolvedCount >= 2`; etc.), People (resolved-by + source), Source context
   checks, keyboard-shortcut callout.
10. `TaskSaveBar` — sticky `.save-bar` with status pill, age, kbd hint, and action buttons.

### New files

- `Beacon.SampleProject/web/src/routes/tasks/parts/TaskHero.tsx`
- `Beacon.SampleProject/web/src/routes/tasks/parts/SlaBanner.tsx`
- `Beacon.SampleProject/web/src/routes/tasks/parts/TaskKpiGrid.tsx`
- `Beacon.SampleProject/web/src/routes/tasks/parts/TaskInfoCard.tsx`
- `Beacon.SampleProject/web/src/routes/tasks/parts/TaskResultChart.tsx`
- `Beacon.SampleProject/web/src/routes/tasks/parts/TaskTabsCard.tsx`
- `Beacon.SampleProject/web/src/routes/tasks/parts/InvestigationLogCard.tsx`
- `Beacon.SampleProject/web/src/routes/tasks/parts/RightRail.tsx` (combines SuggestedNextSteps,
  People, SourceContext, KeyboardTipCallout — three sibling cards in one wrapper)
- `Beacon.SampleProject/web/src/routes/tasks/parts/TaskSaveBar.tsx`

### Hooks added (`routes/tasks/queries.ts`)

- `useTaskExecutionsQuery`, `useTaskRelatedQuery`, `useTaskResultHistoryQuery`,
  `useTaskCommentsQuery`, `useAddTaskComment`. All hand-typed via `fetchJson<T>` —
  matches the existing pattern (codegen integration deferred per Phase 3 batches).

`TaskDetail` interface extended with `lastExecutionAt` and `cronExpression`.

### CSS additions (`web/src/styles-beacon.css`)

Appended a single block ("Task detail additions"):
- `.pill__dot` (was used by the design but not styled)
- `.timeline`, `.tl`, `.tl__rail` (with `::before` rail), `.tl--last`, `.tl__dot`,
  `.tl__dot--{ok,info,warn,err,neutral}`, `.tl__title`, `.tl__sub`, `.tl__time`
- `.sla-meter`, `.sla-meter__fill`, `.banner__actions`
- `.next-step`, `.next-step:hover`, `.next-step__icon`, `.next-step--{warn,ok,info}`,
  `.next-step__title`, `.next-step__sub`
- `.person-row`, `.avatar--muted`, `.person-row__role`, `.person-row__name`, `.person-row__sub`
- `.data-tbl` table styling (HTML `<table>` flavor — distinct from the existing `.tbl` grid)

All values copied verbatim from `Beacon-design/styles.css`. No Tailwind, no shadcn.

### Icons

No additions — every icon the design uses (`Users`, `Bell`, `Check`, `Clock`, `Info`,
`Activity`, `Refresh`, `Plus`, `Bolt`, `Inbox`, `Lightbulb`, `Branch`, `Chevron`, `Dots`,
`Lock`, `Alert`) is already in `Icon.tsx`.

---

## Behavioral diff vs old TaskDetailPage

| | Old (147 LOC) | New |
|---|---|---|
| Layout | Single overview card + optional resolution card | Hero + SLA banner + 4-KPI grid + 2-col layout (4 cards left, right rail) + sticky save-bar |
| Tabs | None | Activity / Executions / Notifications / Related |
| Comments | None | Investigation log composer + comment list (real backend) |
| Chart | None | Result-count progression line chart |
| Right-rail guidance | None | Heuristic next-steps gated on real state, People, Source-context checks, kbd hints |
| Lazy refetch | manual on dialog close | Refresh button + per-query refetch fanout |

---

## Deferred / assumptions

- **SLA window**: 24h hardcoded. No `Sla` field on task or subscription today. If product
  wants a per-subscription SLA, add a column to `Subscription` and surface it through
  `GetTaskDetailHandler`.
- **Assign / Snooze actions**: buttons render but have no handlers — backend has no
  assignment or snooze concept. Wire when those features land.
- **Keyboard shortcuts (R/A/C)**: visualized in callout + save-bar, NOT bound. Out of scope.
- **People → Watchers**: design shows watchers; no backend feature yet.
- **`/tasks/{id}/related` link**: rendered as plain `<a href>` to match the design markup;
  switch to `<Link to=...>` if/when we want SPA navigation between sibling tasks.
- **Rich-text composer toolbar (attach / code / mention)**: design shows placeholder
  buttons; we left them out because none of them have backing features.

---

## Verification

- `dotnet build Beacon.SampleProject -c Release --property WarningLevel=0` — green (4 pre-existing pkg warnings).
- `dotnet test Beacon.Tests -c Release` — green, **35 / 35** passed (count unchanged: the
  OpenAPI contract test is one looping test that now sees five new operationIds).
- `npm run build` — green.
- `npm test` — green, **5 / 5**.
- `web/dist/` rsynced into `Beacon.SampleProject/wwwroot/app/`.
