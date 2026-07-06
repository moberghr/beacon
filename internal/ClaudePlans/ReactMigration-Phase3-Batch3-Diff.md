# React Migration — Phase 3 Batch 3 Behavioral Diff

**Date:** 2026-05-07
**Branch:** `feat/react-phase3`
**Spec:** `ClaudePlans/ReactMigration-Phase3.md` (§ Batch 3)
**Predecessors:** `ClaudePlans/ReactMigration-Phase3-Batch1-Diff.md`, `Phase3-Batch2-Diff.md`

## Goal achieved

Five CRUD pages plus the migration's first mutating flows (RHF + Zod), the first
SignalR consumer, and a reusable dialog system. Seven new MediatR handlers
backing three new endpoint groups. All builds and tests green.

## Pages added

| Route | File | Notes |
|---|---|---|
| `/app/recipients` | `routes/recipients/RecipientsListPage.tsx` | Add / edit / delete. Free-text search. Soft delete via `Archive()`. |
| `/app/tasks` | `routes/tasks/TasksListPage.tsx` | Status filter (unresolved / resolved / all), server-side paging. |
| `/app/tasks/:id` | `routes/tasks/TaskDetailPage.tsx` | Core task fields + Resolve action. Charts/comments/related deferred — see below. |
| `/app/approvals` | `routes/approvals/ApprovalsListPage.tsx` | List + Review dialog with Approve/Reject. Subscribes to `ApprovalUpdated` SignalR event. |
| `/app/api-keys` | `routes/api-keys/ApiKeysListPage.tsx` | Generate dialog reveals raw key once in a copy banner; revoke confirm. CLAUDE.md §1.3 honoured. |
| `/app/users` | `routes/users/UsersListPage.tsx` | Internal/External create tabs, edit, toggle enabled. |

`MIGRATED_PAGES` now adds: `recipients`, `tasks`, `approvals`, `api-keys`,
`users`. The sidebar already had nav items for all five (`Recipients`, `Tasks`,
`API Keys`, `User Management`); they now route through React instead of
falling back to Blazor. Approvals has no sidebar entry by design — Blazor
reached it from query detail; the React page is reachable via direct URL.

## Cross-cutting infrastructure

| File | Role |
|---|---|
| `components/ui/Dialog.tsx` | Portal-rendered dialog shell. Locks body scroll, traps focus on open, restores on close, Esc + backdrop close. Uses existing `.modal-scrim` / `.modal` Beacon-design styles. |
| `components/ui/ConfirmDialog.tsx` | Plain confirmation built on Dialog — used by delete-recipient, revoke-key, and (future) destructive flows. |
| `lib/useHubEvent.ts` | Singleton hub connection + per-component subscription hook. Wraps `connectBeaconHub()`. Connection is shared across consumers; resubscribe on remount. |
| `styles-beacon.css` (appended) | `.modal--sm/md/lg`, `.q-error`, `.q-input--error`, `.btn--danger`, `.api-key-reveal`. No new utility classes — all named, scoped patterns. |

`react-hook-form`, `@hookform/resolvers`, and `zod` were already in
`package.json` from earlier scaffolding; no new dependencies were installed.

## Backend (D3) handlers + endpoints added

### Recipients (`/beacon/api/recipients`)

| Handler | Op id | Method |
|---|---|---|
| `GetRecipientsHandler` | `GetRecipients` | `GET /` |
| `CreateRecipientHandler` | `CreateRecipient` | `POST /` |
| `UpdateRecipientHandler` | `UpdateRecipient` | `PUT /{id}` |
| `DeleteRecipientHandler` | `DeleteRecipient` | `DELETE /{id}` |

All four use `IDbContextFactory<BeaconContext>` directly (no service layer
delegation) so the new code matches §2.6. Delete is soft via `Archive()`
(§2.14). Business-rule violations (name clash, "has subscriptions") throw
`InvalidOperationException` (§9.8).

### Tasks (`/beacon/api/tasks`)

| Handler | Op id | Method |
|---|---|---|
| `GetTasksHandler` (`GetTasksQuery`) | `GetTasks` | `GET /?page&pageSize&resolved&sortColumn&sortDescending` |
| `GetTaskDetailHandler` | `GetTaskDetail` | `GET /{id}` |
| `ResolveTaskHandler` | `ResolveTask` | `POST /{id}/resolve` |

These delegate to `ITaskService` (paging + sorting are non-trivial and the
service already has the right shape — same pattern as Notifications in Batch 2).
The handlers reshape the service DTOs into flat records so the React side has a
flat wire contract.

### Users (`/beacon/api/users`)

| Handler | Op id | Method |
|---|---|---|
| `GetUsersHandler` | `GetUsers` | `GET /?search` |
| `GetRolesHandler` | `GetRoles` | `GET /roles` |
| `CreateInternalUserHandler` | `CreateInternalUser` | `POST /internal` |
| `CreateExternalUserHandler` | `CreateExternalUser` | `POST /external` |
| `UpdateUserHandler` | `UpdateUser` | `PUT /{id}` |
| `ToggleUserEnabledHandler` | `ToggleUserEnabled` | `POST /{id}/toggle-enabled` |

These delegate to `IUserManagementService` and `IRoleService`. Where the
service still uses the legacy `Result<>` shape (`BaseResponse`), the handlers
unwrap and throw `InvalidOperationException` so new code stays consistent with
§2.9 / §9.8 / §9.7 (no new `Result<>` propagation).

### Approvals + API Keys

Already present from earlier work (Phase 1 / Batch 2). No backend changes.

`src/Beacon.SampleProject/Endpoints/BeaconApiEndpoints.cs` registers
`MapRecipientsEndpoints`, `MapTasksEndpoints`, `MapUsersEndpoints`. All inherit
the group `RequireAuthorization(AuthPolicyName)`.

## Frontend conventions established this batch

- **Forms.** Every form uses RHF + Zod. Schemas encode the C# request
  validation (length limits, required, password length 8, email shape). Errors
  render inline below the field via `.q-error`. Submit buttons disable on
  `isSubmitting`.
- **Mutations.** TanStack `useMutation` per write op, with
  `queryClient.invalidateQueries({ queryKey: [...] })` on success. Errors
  surface as `toast.error(...)` from the RFC 7807 / plain text response body.
- **Hand-typed wrappers.** Recipients, Tasks, and Users `queries.ts` use
  `fetchJson<T>()` directly with hand-typed interfaces, NOT the NSwag generated
  client. Reason: the operator's environment cannot run the backend during this
  batch, so `npm run codegen` (which fetches `/openapi/v1.json`) cannot run.
  Approvals and API Keys keep using the generated client because they already
  ship there. **Action item for the user**: run `npm run codegen` after the
  next backend boot to fold the new endpoints into `beacon-api.ts`, then swap
  the manual wrappers in those three `queries.ts` files.
- **SignalR consumer pattern.** `ApprovalsListPage` calls
  `useHubEvent('ApprovalUpdated', () => qc.invalidateQueries({ queryKey: ['approvals'] }))`.
  The hook subscribes on mount, unsubscribes on unmount, and shares one
  underlying WebSocket across the page. The Blazor `BeaconHub` already
  publishes `ApprovalUpdated` from `ApprovalsEndpoints.PublishApprovalUpdated`
  (no server changes needed).
- **API key reveal.** Plaintext key lives in component state for the dialog's
  lifetime only and is wiped on close (§1.3). `Copy` writes to clipboard via
  `navigator.clipboard`; the key is never logged or re-fetched.

## Tests

- **dotnet test:** 35 / 35 green (was 34 before the Tasks rename — see below).
  The OpenAPI contract test (`OpenApiContractTests.EveryMediatRHandlerIsExposedViaHttp`)
  picked up all seven new handlers correctly.
- **vitest:** 3 / 3 green (1 existing + 2 new). The new mutation test
  `RecipientDialog.test.tsx` verifies (a) RHF + MSW POST round-trip and (b)
  Zod required-field validation. Uses `fireEvent.input` instead of
  `@testing-library/user-event` (the latter isn't installed and isn't worth
  adding for two tests).

No translation tests added — the new EF queries (Recipients getter and
Recipients delete projection) are simple `Where + Select` shapes; nothing
joining JSON / arrays / GROUP BY warrants a `ToQueryString()` snapshot per §4.6.
The Tasks queries are wrapped through the existing service, which is already
covered by translation tests in `src/Beacon.Tests/Integration/QueryTranslationTests`.

## Naming gotcha (caught + fixed)

The first cut named the tasks-list query `GetTasksRequestQuery`, which
`OpenApiContractTests` rejected — its heuristic strips `Query` and looks for an
operationId containing `GetTasksRequest`, but the endpoint was named `GetTasks`.
Renamed to `GetTasksQuery` so `StripSuffix` lands on `GetTasks` and matches the
existing endpoint operationId. Lesson: name the request type by the operation,
not by the underlying service DTO.

## Behavioural diff vs. Blazor

| Area | Blazor | React |
|---|---|---|
| Recipients body-template validation | Inline live validation via `TemplateValidator.ValidateWithPlaceholderCheck` | Skipped client-side; the server still rejects on save and the error is surfaced via toast. The validator lives server-side and isn't worth duplicating in JS for Batch 3. |
| Tasks list | MudDataGrid `ServerData` with column-level sorting + `Filterable` | Single sort (`CreatedAt desc`), three-button status filter, Prev/Next paging. Sortable columns are a Batch 4/5 add (DataTable doesn't support headers-as-buttons yet). |
| Task detail | Hero stat cards + result-count chart + execution history + comments + related tasks | Core fields + resolve action only. Charts, comments, related, and reopen are deferred (see below). |
| Approvals diff view | `MudCard` per step diff with Modified/Added/Removed colour swatches | Single proposed-SQL block (`detail.proposedVersion.finalQuery`). The full step-by-step diff is deferred to Batch 5 (QueryEditor lands there). |
| Generate API key | Project-restriction multiselect + scope checkboxes + expiration | Scope checkboxes + expiration only. Project restrictions are deferred — would need a `getProjects` dependency in the dialog and a Zod schema branch. Not a security regression; the backend simply receives `allowedProjectIds: null`. |
| User management | Role multi-select + password complexity hints + show/hide password toggle | Plain checkbox role list + simple password length validation. Server-side `IUserManagementService` still enforces the configured password policy and returns errors via toast. |
| User toggle enabled | `BeaconSwitch` per row | Status pill is the click target. |

## Deferred to Batch 4

These were intentionally trimmed during Batch 3 to keep scope manageable;
nothing security-critical was dropped.

- Task detail: hero stat cards, result-count line chart, execution history
  table, comments, related tasks, reopen action.
- Approvals: full step-by-step `QueryVersionDiff` rendering with side-by-side
  before/after for each step. Currently only the proposed final SQL is shown.
- API key generation: project restrictions and richer scope helper text.
- User dialog: password complexity client-side checking (server enforces it).
- Recipients body-template live validator (inline error messages on the
  template field).
- DataTable sortable headers (touches every list page).

## Files touched

### Backend
- `src/Beacon.Core/Handlers/Recipients/{GetRecipients,CreateRecipient,UpdateRecipient,DeleteRecipient}Handler.cs` (new)
- `src/Beacon.Core/Handlers/Tasks/{GetTasks,GetTaskDetail,ResolveTask}Handler.cs` (new)
- `src/Beacon.Core/Handlers/Users/{GetUsers,GetRoles,CreateInternalUser,CreateExternalUser,UpdateUser,ToggleUserEnabled}Handler.cs` (new)
- `src/Beacon.SampleProject/Endpoints/{Recipients,Tasks,Users}Endpoints.cs` (new)
- `src/Beacon.SampleProject/Endpoints/BeaconApiEndpoints.cs` (registered three new groups)

### Frontend
- `src/Beacon.SampleProject/web/src/components/ui/{Dialog,ConfirmDialog}.tsx` (new)
- `src/Beacon.SampleProject/web/src/lib/useHubEvent.ts` (new)
- `src/Beacon.SampleProject/web/src/routes/recipients/{queries.ts,RecipientDialog.tsx,RecipientsListPage.tsx,RecipientDialog.test.tsx}` (new)
- `src/Beacon.SampleProject/web/src/routes/tasks/{queries.ts,ResolveTaskDialog.tsx,TasksListPage.tsx,TaskDetailPage.tsx}` (new)
- `src/Beacon.SampleProject/web/src/routes/approvals/{queries.ts,ReviewApprovalDialog.tsx,ApprovalsListPage.tsx}` (new)
- `src/Beacon.SampleProject/web/src/routes/api-keys/{queries.ts,GenerateApiKeyDialog.tsx,ApiKeysListPage.tsx}` (new)
- `src/Beacon.SampleProject/web/src/routes/users/{queries.ts,UserDialog.tsx,UsersListPage.tsx}` (new)
- `src/Beacon.SampleProject/web/src/styles-beacon.css` (appended Batch 3 helpers)
- `src/Beacon.SampleProject/web/src/feature-flags.ts` (added five slugs)
- `src/Beacon.SampleProject/web/src/App.tsx` (six new lazy routes)
- `src/Beacon.SampleProject/wwwroot/app/` (rsynced from `dist/`)

## Acceptance gate result

| Gate | Status |
|---|---|
| `dotnet build -c Release --property WarningLevel=0` | green |
| `dotnet test` (full solution) | 35 / 35 green |
| `npm run build` | green |
| `npm test` | 3 / 3 green |
| Manual browser smoke | pending (requires backend + `npm run codegen`) |
| Diff document written | this file |
| `git commit` | pending — final step of this batch |
