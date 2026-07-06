# Task Detail Redesign — Diff

Phase 3 follow-up to Batch 5a. Replaces the placeholder Task Detail page with the
"signal" multi-card layout from `Beacon-design/task-detail.jsx`.

Branch: `feat/react-phase3` (continues from 5a backend).

---

## Backend (D3)

### New MediatR slices (one file, internal sealed + primary ctor + records)

| File | Wraps | Endpoint |
|---|---|---|
| `src/Beacon.Core/Handlers/Tasks/GetTaskExecutionsHandler.cs` | `taskService.GetTaskExecutionHistory` | `GET /tasks/{id}/executions` |
| `src/Beacon.Core/Handlers/Tasks/GetTaskRelatedHandler.cs` | `taskService.GetRelatedTasks` | `GET /tasks/{id}/related` |
| `src/Beacon.Core/Handlers/Tasks/GetTaskResultHistoryHandler.cs` | `taskService.GetResultCountHistory` | `GET /tasks/{id}/result-history` |
| `src/Beacon.Core/Handlers/Tasks/GetTaskCommentsHandler.cs` | `taskService.GetTaskComments` | `GET /tasks/{id}/comments` |
| `src/Beacon.Core/Handlers/Tasks/AddTaskCommentHandler.cs` | `taskService.AddTaskComment` (with `IBeaconUserContext`) | `POST /tasks/{id}/comments` |

All return shapes are dedicated records (`TaskExecutionsResult`, `TaskRelatedResult`,
`TaskResultHistoryResult`, `TaskCommentsResult`, `AddTaskCommentResult`) — service DTOs
are not leaked through MediatR. `AddTaskCommentHandler` throws `InvalidOperationException`
on whitespace content.

### Enriched existing handler

`src/Beacon.Core/Handlers/Tasks/GetTaskDetailHandler.cs` — added two fields to `TaskDetailResult`:
- `LastExecutionAt: DateTime?` — most recent `QueryExecutionHistory` row for the parent subscription.
- `CronExpression: string?` — parent subscription's `CronExpression`.

`src/Beacon.Core/DTOs/TaskDetailsData.cs` — added matching properties.

`src/Beacon.Core/Services/TaskService.cs::GetTaskDetails` — extended the existing single
`.Select(new ...)` projection to populate both fields. No new round-trip; the LastExecutionAt
sub-query is a correlated subquery that EF translates to a `LATERAL` lookup on Postgres.

### Endpoint wiring

`src/Beacon.SampleProject/Endpoints/TasksEndpoints.cs` — appended five endpoints with names
matching the OpenAPI contract heuristic (strip `Command`/`Query`):

- `GetTaskExecutions`, `GetTaskRelated`, `GetTaskResultHistory`, `GetTaskComments`, `AddTaskComment`.

Auth is inherited from the parent group (`MapBeaconApi` already calls `.RequireAuthorization(AuthPolicyName)`).

---

## Frontend

### Page replaced

`src/Beacon.SampleProject/web/src/routes/tasks/TaskDetailPage.tsx` — fully rewritten. Layout:

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

- `src/Beacon.SampleProject/web/src/routes/tasks/parts/TaskHero.tsx`
- `src/Beacon.SampleProject/web/src/routes/tasks/parts/SlaBanner.tsx`
- `src/Beacon.SampleProject/web/src/routes/tasks/parts/TaskKpiGrid.tsx`
- `src/Beacon.SampleProject/web/src/routes/tasks/parts/TaskInfoCard.tsx`
- `src/Beacon.SampleProject/web/src/routes/tasks/parts/TaskResultChart.tsx`
- `src/Beacon.SampleProject/web/src/routes/tasks/parts/TaskTabsCard.tsx`
- `src/Beacon.SampleProject/web/src/routes/tasks/parts/InvestigationLogCard.tsx`
- `src/Beacon.SampleProject/web/src/routes/tasks/parts/RightRail.tsx` (combines SuggestedNextSteps,
  People, SourceContext, KeyboardTipCallout — three sibling cards in one wrapper)
- `src/Beacon.SampleProject/web/src/routes/tasks/parts/TaskSaveBar.tsx`

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
- `web/dist/` rsynced into `src/Beacon.SampleProject/wwwroot/app/`.

---

## Wire-up follow-up

All deferred mocks from the section above have been replaced with real backend.

### Schema (Fluent API only — §5.4)

- `QueryTask` gained `AssigneeUserId: string?` (max 100), `SnoozedUntil: DateTime?`,
  `Priority: TaskPriority` (int-converted, default `Normal`). Indexes on each.
- `Subscription` gained `SlaHours: int?` — null means "use system default (24h)".
- New enum `src/Beacon.Core/Data/Enums/TaskPriority.cs` (`Critical=1`, `High=2`, `Normal=3`, `Low=4`).
- New entity `src/Beacon.Core/Data/Entities/TaskWatcher.cs` — composite key
  `(QueryTaskId, UserId)`, no soft delete (watchers are direct-removable).
  Cascade delete from parent task. `BeaconContext` exposes `DbSet<TaskWatcher> TaskWatchers`.
  Configured via new `ConfigureTaskWatcherEntities()` partial in `BeaconContext`.

### Migrations (one per provider — §0.1, §5.9)

- `src/Beacon.Core.PostgreSql/Data/Migrations/20260508125035_TaskAssignSnoozePriorityWatchersSubscriptionSla.cs`
- `src/Beacon.Core.SqlServer/Data/Migrations/20260508125116_TaskAssignSnoozePriorityWatchersSubscriptionSla.cs`

Both add columns + the `task_watchers`/`TaskWatchers` table with composite PK and the
expected indexes. Generated via the documented `Program.cs`-swap workflow; provider
flag reverted to PostgreSQL after.

### New MediatR slices

| File | Command | Endpoint |
|---|---|---|
| `Handlers/Tasks/AssignTaskHandler.cs` | `AssignTaskCommand(int, string?)` | `POST /tasks/{id}/assign` |
| `Handlers/Tasks/SnoozeTaskHandler.cs` | `SnoozeTaskCommand(int, DateTime?)` | `POST /tasks/{id}/snooze` |
| `Handlers/Tasks/SetTaskPriorityHandler.cs` | `SetTaskPriorityCommand(int, TaskPriority)` | `POST /tasks/{id}/priority` |
| `Handlers/Tasks/WatchTaskHandler.cs` | `WatchTaskCommand(int)` (idempotent, uses `IBeaconUserContext`) | `POST /tasks/{id}/watch` |
| `Handlers/Tasks/UnwatchTaskHandler.cs` | `UnwatchTaskCommand(int)` (idempotent) | `POST /tasks/{id}/unwatch` |
| `Handlers/Subscriptions/SetSubscriptionSlaHandler.cs` | `SetSubscriptionSlaCommand(int, int?)` (validates 1–720) | `POST /subscriptions/{id}/sla` |

All `internal sealed` + primary-ctor + `IDbContextFactory<BeaconContext>`. Throw
`InvalidOperationException` on missing entity / invalid input. Idempotent watch/unwatch
(no-op if already in target state). `SnoozeTaskHandler` rejects past timestamps.
`SetSubscriptionSlaHandler` enforces 1 ≤ slaHours ≤ 720 (30 days). All endpoints
inherit `.RequireAuthorization()` from the parent `MapBeaconApi` group.

### `GetTaskDetailHandler` enrichment

`TaskDetailResult` and `TaskDetailsData` extended with `Priority`, `AssigneeUserId`,
`AssigneeUserName`, `SnoozedUntil`, `SlaHours`, `WatcherCount`, `IsWatching`,
`OwnerUserId`, `OwnerUserName`. The handler now reads `IBeaconUserContext.UserId`
and forwards it to the service so `IsWatching` is evaluated server-side.

`ITaskService.GetTaskDetails` signature changed to
`GetTaskDetails(int taskId, string? currentUserId, CancellationToken)`. The single
existing caller in `src/Beacon.UI/Components/Pages/Tasks/TaskDetails.razor` was updated
to pass `null` (the legacy Blazor task-detail page has no per-user watcher concept).

The projection stays a single `.Select(new ...)` round-trip. Username lookups for
`AssigneeUserName`, `ResolvedByUserName`, `OwnerUserName` are correlated subqueries
against `BeaconUser` keyed on `ExternalId` (which matches the `ClaimTypes.NameIdentifier`
form already used by `ResolvedByUserId`). Owner is null for user-created subscriptions
because `Subscription` has no `CreatedByUserId` audit field — only AI-actor-managed
subscriptions surface an owner today (via `AiActor.CreatedByUserId`). Documented as a
future-cleanup item if product wants user attribution on user-created subscriptions.

### Frontend (`src/Beacon.SampleProject/web/src/routes/tasks/`)

`queries.ts` extensions:

- `TaskDetail` interface gained `priority`, `assigneeUserId`, `assigneeUserName`,
  `snoozedUntil`, `slaHours`, `watcherCount`, `isWatching`, `ownerUserId`, `ownerUserName`.
- New `TaskPriority` numeric union type aligned with the C# enum.
- New hooks: `useAssignTask`, `useSnoozeTask`, `useSetTaskPriority`, `useWatchTask`,
  `useUnwatchTask`, `useSetSubscriptionSla`. All invalidate `['task', id]` and
  `['tasks']` on success and surface errors (incl. RFC 7807 body) via `sonner`.
- Detail-query key normalized to `['task', id]` (was `['tasks', 'detail', id]`).
- `useResolveTask` now also invalidates the per-task detail query.

UI wiring:

- **`TaskHero`**: real status pill (OPEN / SNOOZED / RESOLVED), real priority pill
  (P1–P4 with severity color), real source pill (`aiActorName ?? "USER-DEFINED"`).
  Assign button opens a popover with "Assign to me" / "Unassign" entries. Snooze
  button opens a popover with 1h / 4h / 24h presets, a "Wake now" entry when
  currently snoozed, and a custom datetime-local picker.
- **`SlaBanner`**: SLA window now `task.slaHours ?? 24`. Banner suffix marks default
  vs custom. Admin gear (gated on `useIsAdmin()`) opens an inline editor that calls
  `useSetSubscriptionSla`; "Use default" sends `slaHours: null`.
- **`TaskInfoCard`**: Priority is now a `<select>` wired to `useSetTaskPriority`
  (disabled when resolved). Assignee row shows the real assignee with a "claim"
  link calling `useAssignTask({ assigneeUserId: currentUser.userId })`. New
  "Snoozed until" row.
- **`RightRail`**: "Claim ownership" next-step now gates on `!task.assigneeUserId`
  (real condition). People card shows real Assignee + Owner + Resolved-by, and a
  Watchers row with live count and a Watch / Watching toggle button wired to
  `useWatchTask` / `useUnwatchTask`. Keyboard-shortcut callout updated to include `S`.
- **`TaskSaveBar`**: Snooze and Assign buttons fire real mutations directly (no
  passed-in handlers). "Assign to me" flips to "Unassign me" when already assigned to
  the current user. Hint text now lists R / A / S / C.
- **`TaskDetailPage`**: binds R / A / S / C keyboard shortcuts on `document` via a
  `useEffect`, ignoring events from input/textarea/select/contenteditable so typing
  in the composer doesn't trigger them. C focuses the composer textarea by id.
- **`InvestigationLogCard`**: accepts an optional `textareaId` prop so the page can
  target it from the C shortcut. The composer is already toolbar-free
  (attach/code/mention buttons were never built — design carry-over only).

### Activity timeline — deliberate non-coverage

Assign / Unassign / Snooze / Unsnooze / Priority change / Watch / Unwatch
intentionally do NOT show up in the Activity tab. There is no task-event audit log
table today, and `Comment`/execution-history won't capture these. Adding fake
client-synthesized entries would violate §9.1 (no fake data). The Activity tab
remains the merge of: task creation + executions + comments + resolution.
Surfacing these mutations in the timeline requires a follow-up: add a
`QueryTaskEvent` entity (or extend the `Comment` table with a `Kind` discriminator)
and emit rows from the new handlers.

### Verification (after wire-up)

- `dotnet build Beacon.SampleProject -c Release --property WarningLevel=0` — green.
- `dotnet test Beacon.Tests -c Release` — green, **35 / 35**. The OpenAPI contract
  test picks up the six new operationIds (AssignTask, SnoozeTask, SetTaskPriority,
  WatchTask, UnwatchTask, SetSubscriptionSla).
- `npm run build` — green.
- `npm test` — green, **5 / 5**.
- After clearing `*.dswa.cache.json` in `src/Beacon.SampleProject/obj/` (stale-asset
  cache item from `migrations-workflow.md`), build is clean.

### Caveats / future cleanup

- `Subscription` lacks a `CreatedByUserId`. Owner attribution only works for
  AI-managed subscriptions today.
- The numeric warning at migration generation about `Priority`'s sentinel is benign:
  the column has a DB default of `3` (Normal) and inserts go through tracked
  entities, so the default kicks in only on raw inserts that omit the column.
- Custom snooze input uses `datetime-local`; the value is converted to ISO UTC
  before being sent to the server.
- Watch/Unwatch idempotency lives in the handlers; the UI happily re-fires either,
  guarded by the local mutation `isPending`.
