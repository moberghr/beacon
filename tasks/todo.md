# Todo — React Migration Phase 3 Batch 3 (Simple CRUD)

**Spec:** `ClaudePlans/ReactMigration-Phase3.md` (Batch 3 section)
**Predecessor diffs:** `ClaudePlans/ReactMigration-Phase3-Batch1-Diff.md`, `Phase3-Batch2-Diff.md`
**Branch:** `feat/react-phase3` (continue — push to existing draft PR #13)
**Worktree:** `/Users/mirkobudimir/Dev/MiBu/semantico-react`

---

## Critical constraints (carry over from Batches 1 & 2)
- **NO Tailwind, NO shadcn primitives.** Beacon-design CSS in `web/src/styles-beacon.css`. Add patterns there if missing.
- **NO fake/seed/demo data.** Pages and forms start empty; populate from real endpoints.
- All routes lazy-loaded; one file per page in `routes/<area>/`.
- Tables via `<DataTable>`, icons via `Icon.Xxx` (extend `Icon.tsx`), formatters via `lib/format.ts`.
- API via `beaconApi()` singleton + TanStack Query hooks per area.
- Add migrated slug to `web/src/feature-flags.ts` `MIGRATED_PAGES` only after the page renders end-to-end.
- **In-app `<Link>` and `navigate()` paths must NOT include `/app/` prefix** — `BrowserRouter basename="/app"` adds it.
- After every React-only build, sync `web/dist/` → `wwwroot/app/` (or run `dotnet build` to trigger the copy target).
- Backend: MediatR handler+request+result in one file as `internal sealed class` + primary ctor; `IDbContextFactory<BeaconContext>`; `.Select(new ...)` without `.Include()`; `InvalidOperationException`/`BeaconException` (no Result pattern); Beacon C# style guide.

## New patterns introduced in Batch 3
- **First mutations.** TanStack Query `useMutation` invalidating queries on success.
- **React Hook Form + Zod** for every form. `@hookform/resolvers/zod`. Encode handler validation in Zod.
- **First SignalR consumer.** `lib/hub.ts` already exists; subscribe to the approval-updated hub event and call `queryClient.invalidateQueries({queryKey: ['approvals']})`.
- **Dialog component.** Add `components/ui/Dialog.tsx` to Beacon-design (or whatever pattern matches Blazor's MudDialog). One reusable shell — header, body, footer with primary/cancel.
- **Toast on mutation.** Sonner already wired; surface success/error via `toast.success` / `toast.error` from RFC 7807 response.

---

## Pages to ship

### 3.1 — Recipients (`/app/recipients`)
Blazor: `Recipients.razor`, dialogs `AddRecipientDialog.razor`, `AddRecipientsDialog.razor`, `UpdateRecipientDialog.razor`.

- [x] Audit Blazor page + dialogs to list endpoints used and field shapes.
- [x] D3 if needed: add MediatR handlers (`GetRecipients`, `CreateRecipient`, `UpdateRecipient`, `DeleteRecipient`) + endpoints under `/beacon/api/recipients`.
- [x] `routes/recipients/RecipientsListPage.tsx` — DataTable, click row → edit dialog. Top-right "Add recipient" button.
- [x] `routes/recipients/RecipientDialog.tsx` — RHF + Zod, used for both Add and Update.
- [x] `routes/recipients/queries.ts` — `useRecipientsQuery`, `useCreateRecipient`, `useUpdateRecipient`, `useDeleteRecipient`.
- [x] `MIGRATED_PAGES += 'recipients'`. Lazy-route. Smoke test.

### 3.2 — Tasks (`/app/tasks` + `/app/tasks/:id`)
Blazor: `Tasks.razor`, `TaskDetails.razor`, `ResolveTaskDialog.razor`.

- [x] D3 if needed: handlers for `GetTasks`, `GetTaskDetail`, `ResolveTask`.
- [x] `routes/tasks/TasksListPage.tsx` — DataTable with status filter, click → detail.
- [x] `routes/tasks/TaskDetailPage.tsx` — fields + "Resolve" action opens dialog.
- [x] `routes/tasks/ResolveTaskDialog.tsx` — RHF + Zod.
- [x] `MIGRATED_PAGES += 'tasks'`. Lazy-routes. Smoke.

### 3.3 — Pending approvals (`/app/approvals`) — FIRST SIGNALR
Blazor: `PendingApprovals.razor`, `ReviewApprovalDialog.razor`.

- [x] Endpoints exist or need D3? Confirm.
- [x] `routes/approvals/ApprovalsListPage.tsx` — list, click → review dialog.
- [x] `routes/approvals/ReviewApprovalDialog.tsx` — RHF + Zod, approve/reject with optional comment.
- [x] **SignalR**: subscribe to the existing approval-updated hub event in this page (or in `queries.ts`). On event → `queryClient.invalidateQueries(['approvals'])`. Cleanup on unmount.
- [x] `MIGRATED_PAGES += 'approvals'`. Smoke including the realtime path.

### 3.4 — API keys (`/app/api-keys`)
Blazor: `ApiKeys.razor`, `GenerateApiKeyDialog.razor`.

- [x] List with scopes + status. Generate dialog returns the raw key ONCE; show in a copy-to-clipboard banner. Per CLAUDE.md §1.3, never echo or log the raw key.
- [x] Revoke action with confirmation prompt.
- [x] `MIGRATED_PAGES += 'api-keys'`. Smoke.

### 3.5 — Users (`/app/users`)
Blazor: `Users.razor`, `AddUserDialog.razor`, `UpdateUserDialog.razor`.

- [x] List with roles. Add/update dialogs (RHF + Zod). Delete with confirm.
- [x] If user editing requires admin role, gate the page (route-level + button-level).
- [x] `MIGRATED_PAGES += 'users'`. Smoke.

---

## Cross-cutting
- [x] `components/ui/Dialog.tsx` — single dialog shell (header + body + footer). Beacon-design CSS only.
- [x] `components/ui/ConfirmDialog.tsx` — used by destructive actions (delete recipient, revoke key, delete user).
- [x] `lib/hub.ts` consumer hook helper, e.g. `useHubEvent('approvalUpdated', cb)` — clean up on unmount.
- [x] Vitest: add at least one mutation test (RecipientDialog) using MSW to mock POST.

---

## Acceptance gate
- [x] `dotnet build -c Release --property WarningLevel=0` green
- [x] `dotnet test` — all green; new translation tests for any non-trivial new query (per §4.6); OpenAPI contract test still passes for every new handler
- [x] `npm run build` green
- [x] `npm test` green (existing + new mutation test)
- [ ] Manual browser smoke each new page (CRUD + SignalR) — pending user verification + `npm run codegen` against a running backend
- [x] `ClaudePlans/ReactMigration-Phase3-Batch3-Diff.md` written
- [x] `git commit` only — DO NOT push (user pushes manually)

---

## Out of scope (deferred)
- Project detail repositories/docs/ai-actors content — Batch 4
- Subscriptions, DataSources — Batch 4
- QueryDetails, QueryEditor — Batch 5
- Settings/AdminSettings — Batch 4
