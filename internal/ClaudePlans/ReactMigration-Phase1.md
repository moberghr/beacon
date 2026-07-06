# React Migration — Phase 1 Spec

**Status:** Draft, awaiting approval
**Author:** planning session 2026-05-07
**Branch:** `feature/react-migration-phase1` (off `feature/react-migration`)
**PR target:** `main` (after Phase 0 PR #9 merges; rebase will be required)
**Predecessor:** Phase 0 — `ClaudePlans/ReactMigration-Phase0-1.md` (foundations) + `ClaudePlans/ReactMigration-Phase0-Diff.md` (delivered behaviour)

---

## Goal

Wrap every existing MediatR handler in an HTTP endpoint at `/beacon/api/*`, add a SignalR push channel for real-time UX, fix the cookie-auth-for-API JSON-401 issue, stand up an integration test harness, and emit a typed TypeScript client. After Phase 1, a React page can call any current backend operation without going through Blazor.

**Phase 1 does NOT migrate any Blazor page** — that is Phase 3.

## Locked decisions (carry from Phase 0)

| # | Decision | Choice |
|---|---|---|
| D1 | Hosting | Same-origin. React at `/app/*`, REST at `/beacon/api/*`. Verified working in Phase 0. |
| D2 | UI framework | Shadcn/ui + Tailwind + TanStack Table + TanStack Query + RHF + Zod + React Router. |
| D3 | Real-time | **SignalR.** One hub at `/beacon/api/hub`. `@microsoft/signalr` client. |
| D4 | Type contracts | NSwag → TypeScript from `/openapi/v1.json`. **Generated client committed in Phase 1** (was gitignored in Phase 0). CI drift check enforced. |

## Audit-anchored facts

From the handler audit conducted 2026-05-07:

- **81 MediatR handlers total**, distributed:
  - Projects: 6 (3 Core + 3 AI). The other AI ones wrap Core handlers.
  - QueryFolders: 5
  - Queries: 2
  - QueryVersions: 4
  - Approvals: 4
  - ApiKeys: 3
  - Dashboards: 11 (incl. widgets, permissions, sharing, clone)
  - DataQuality: 7
  - MCP Settings: 2
  - McpLearning: 5
  - DataCatalog: 1
  - AiActors: **11 distinct endpoints** (Core + AI handler classes overlap; one wraps the other via DI — the registered handler wins, the other is dead until configured)
- **Total distinct HTTP endpoints to ship in Phase 1: ~61** (after collapsing AiActor overlaps).
- **No EF entities leak through MediatR results.** All result records are flat DTOs. Confirmed in audit.
- **Several handlers do non-trivial work that affects HTTP shape:**
  - `ExportProjectDocumentationHandler` → returns `(Content, MimeType, FileName)`. HTTP wrapper streams content with the right `Content-Disposition`.
  - `GenerateProjectDocumentationHandler` → returns a Hangfire `JobId`; the SignalR hub publishes progress.
  - `InstructDocumentationHandler` → calls LLM via `LlmRequestQueue`. Long-running. Kestrel timeout (5 min, §6.3) is sufficient.
  - `ScanAllRepositoriesHandler` → batch op; returns `(Scanned, Errors)`. Plain JSON.
  - All Dashboard handlers check `DashboardPermissionLevel` internally; HTTP layer just calls and lets exceptions bubble through the new exception middleware.
- **No subscription, task, recipient, notification, datasource, or migration handlers exist** — those pages call services directly. Phase 3 will add MediatR handlers for those features as part of page migration. Phase 1 ships only what exists.

---

## Phase 1 acceptance criteria

1. **All 61 distinct endpoints reachable** under `/beacon/api/*`, each with `.RequireAuthorization()` (none anonymous), each with a stable `WithName(...)` and `WithTags(...)`.
2. **OpenAPI contract test** scans `Beacon.Core/Handlers/**` and `Beacon.AI/Handlers/**`, asserts every distinct request type maps to an endpoint. Adding a handler without an endpoint fails CI.
3. **NSwag drift test** asserts `web/src/api/generated/beacon-api.ts` is byte-identical to a fresh codegen. Modifying the OpenAPI doc without regenerating the TS file fails CI.
4. **Integration test harness** running against `WebApplicationFactory<Program>` with a test SQLite/Postgres backing. At least 5 endpoint smoke tests covering: anon/auth, error mapping, file download, action verb, JSON 401.
5. **JSON 401 for `/api/*`**: anonymous calls to `RequireAuthorization` endpoints return `401 application/json` with `{ "error": "Unauthorized" }`, not an HTML redirect.
6. **Exception middleware** maps:
    - `InvalidOperationException` → 400 with `{ "error": message }`
    - `BeaconException` → 400 (or `BeaconException.StatusCode` if set) with `{ "error", "code" }`
    - `UnauthorizedAccessException` → 403 with `{ "error": "Forbidden" }`
    - Anything else → 500 with `{ "error": "Internal server error" }`. Real exception logged with context, never returned. (§1.11.)
7. **SignalR hub at `/beacon/api/hub`**, authorized, publishing to `Clients.User(...)` only:
    - `JobStatusChanged` (Hangfire filter publishes on enqueue/process/succeeded/failed)
    - `NotificationCreated` (`INotificationService` publishes after store)
    - `ApprovalUpdated` (`ApproveQueryChange` / `RejectQueryChange` handlers publish after commit)
8. **`dotnet build -c Release` + `dotnet test` + `npm run build` all green**.
9. **Generated TS client committed**, plus a `web/src/api/client.ts` thin wrapper that:
    - Reads CSRF cookie and sets `X-XSRF-TOKEN` on mutations
    - Sets `credentials: 'include'`
    - Maps 401 → `signOut()` → redirect to `/beacon`
    - Maps `ApiException` properly so React Query's `isError` and the error body match
10. **`/api/auth/permissions` returns 401 (not 500)** for anonymous callers — direct fix from the Phase 0 caveat.

---

## Out of scope (deferred)

- **No Blazor page migrated.** Blazor UI continues to call `IMediator` directly. The HTTP layer is additive.
- **No new MediatR handlers** for Subscriptions/Tasks/Recipients/Notifications/DataSources/DataMigration. Those land in Phase 3 alongside their pages.
- **No CORS.** Same-origin (D1).
- **No bearer JWT.** Cookie auth (§1.8 / §1.9).
- **No rename of existing endpoints** — `LoginEndpoints.cs` and `SetupEndpoints.cs` stay where they are. Their decommission is part of Phase 4 cutover.
- **No DTO renames or shape changes.** MediatR result records become JSON contracts as-is. If a record name is awkward for HTTP, log it and address in Phase 2 (a polish phase between API surface and page migration).

---

## Architecture changes

### `Beacon.SampleProject/Endpoints/` grows

```
Beacon.SampleProject/
├── Endpoints/
│   ├── BeaconApiEndpoints.cs        # composition root (existing, expanded)
│   ├── HealthEndpoints.cs           # existing
│   ├── AuthEndpoints.cs             # existing, gains JSON 401 hooks
│   ├── AntiforgeryEndpoints.cs      # existing
│   ├── ProjectsEndpoints.cs         # NEW — wraps 6 handlers
│   ├── QueryFoldersEndpoints.cs     # NEW — wraps 5
│   ├── QueriesEndpoints.cs          # NEW — wraps 2
│   ├── QueryVersionsEndpoints.cs    # NEW — wraps 4
│   ├── ApprovalsEndpoints.cs        # NEW — wraps 4
│   ├── ApiKeysEndpoints.cs          # NEW — wraps 3
│   ├── DashboardsEndpoints.cs       # NEW — wraps 11
│   ├── DataQualityEndpoints.cs      # NEW — wraps 7
│   ├── McpSettingsEndpoints.cs      # NEW — wraps 2
│   ├── McpLearningEndpoints.cs      # NEW — wraps 5
│   ├── DataCatalogEndpoints.cs      # NEW — wraps 1
│   ├── AiActorsEndpoints.cs         # NEW — wraps 11
│   └── ProblemDetailsExtensions.cs  # NEW — exception → ProblemDetails mapping
├── Hubs/
│   └── BeaconHub.cs                 # NEW — SignalR hub
├── SignalR/
│   ├── HubUserIdProvider.cs         # NEW — resolves Beacon user id from claim
│   └── HangfireSignalRFilter.cs     # NEW — JobFilterAttribute publishing job state
└── Middleware/
    └── ApiExceptionMiddleware.cs    # NEW — exception → JSON
```

### Endpoint conventions

- **One endpoint = one MediatR handler.** Always. No composition at the endpoint layer.
- **`POST` for commands, `GET` for queries, `PUT` for updates, `DELETE` for archive.** Action verbs (`approve`, `reject`, `pause`, `resume`, `evaluate`, `scan-repositories`, `think`, `restore`, `apply`, `clone`) become `POST /resource/{id}/{verb}`.
- **Path-bound IDs** (e.g., `{id}`) are the FIRST positional field on the request record. Body carries the rest. Endpoint must validate `id` from path matches `Id` on body if both present (defensive).
- **Cancellation tokens** plumbed through to `IMediator.Send(req, ct)` in every endpoint.
- **`.RequireAuthorization()` always.** No anonymous endpoints in `/beacon/api/*` from Phase 1 onwards. (Health, auth/me, csrf already exist as exceptions.)
- **`.WithName(...)` is `{Operation}{Domain}`** — e.g., `GetProjects`, `CreateProject`, `ApproveQueryChange`. Drives NSwag method names.
- **`.WithTags("Projects")`** etc. — drives NSwag class grouping → `ProjectsClient`.

### Exception → JSON shape (RFC 7807-aligned)

```json
{
  "type": "/errors/business-rule",
  "title": "Folder not found.",
  "status": 400,
  "code": "BEACON_FOLDER_NOT_FOUND"
}
```

`code` only set when `BeaconException` carries one. `type` is constant per category. `detail` is omitted (PII safety, §1.11).

### SignalR hub interface

```typescript
// React side, declared in beacon-hub.ts
interface BeaconHubClient {
  on(event: 'JobStatusChanged', handler: (msg: { jobId: string; state: string; timestamp: string }) => void): void;
  on(event: 'NotificationCreated', handler: (msg: { notificationId: number; kind: string }) => void): void;
  on(event: 'ApprovalUpdated', handler: (msg: { approvalId: number; status: string }) => void): void;
}
```

Server side: `IHubContext<BeaconHub>` injected where needed; payloads are flat records; clients only get events for their own user id via `Clients.User(...)`.

---

## Test strategy

1. **Unit tests** for `ApiExceptionMiddleware` mapping (NUnit + Moq).
2. **Integration tests** in a new `Beacon.Tests/Integration/Api/` directory using `WebApplicationFactory<Program>` (NUnit). Test sample per feature area:
    - One GET (auth required) — anon → 401 JSON, auth → 200
    - One POST (validates antiforgery token)
    - One DELETE (soft-archive)
    - The file-download path (`ExportProjectDocumentation`) — verifies `Content-Disposition`
    - The Hangfire-backed path (`GenerateProjectDocumentation`) — verifies a `jobId` is returned (mock the actual scheduler in tests)
3. **Contract test** (the OpenAPI scan) lives at `Beacon.Tests/Integration/Api/OpenApiContractTests.cs`. Reflects on `IRequest<>` types and asserts each has a route.
4. **Drift test** for NSwag lives in the same file; runs `nswag run nswag.config.json` against the in-process host's OpenAPI document, diffs against committed `beacon-api.ts`.
5. **Existing 28 tests** must continue to pass.

### Test database strategy

`WebApplicationFactory<Program>` creates a test host. Override config to use:
- A SQLite-backed BeaconContext (PostgreSQL provider's SQL would be wrong for sqlite, but `InMemoryDatabase` is forbidden by §4.7). Compromise: spin up a real PostgreSQL via the existing `BeaconContext` with a `.testing` connection string — assumes a Postgres instance is available locally.
- **Better**: use the existing `appsettings.Development.json` connection but point at a uniquely-named test schema (`beacon_test_{guid}`). Migrations run on test fixture startup, drop on teardown.

This is itself a non-trivial setup. Initial Phase 1 batches will mock `IMediator` in tests rather than wire a real database; full integration coverage gets a follow-up batch.

---

## Risks

| Risk | Mitigation |
|---|---|
| 61 endpoints × 5 review iterations is a huge PR | Batch the work in `tasks/todo.md`. Each batch can be independently reviewed. PR opens once all batches green. |
| AiActor handler duplication (Core + AI) — wrong one wins | Inspect DI registration; document which is canonical; do not call the duplicate from the endpoint. |
| Cookie auth's `OnRedirectToLogin` doing HTML redirects breaks REST clients | Implement on day one (Batch 1). Test path: anon GET to `/beacon/api/auth/permissions` → 401 JSON. |
| OpenAPI document grows; NSwag emits a 100KB+ TS file | Acceptable; Vite tree-shakes per-method imports. Monitor bundle size at end of Phase 1. |
| LLM-calling handlers (`InstructDocumentation`) hit Kestrel timeouts | Already 5 min (§6.3). If a handler legitimately needs longer, fire SignalR events instead of holding the request open — same pattern as `GenerateProjectDocumentation`. |
| File download (`ExportProjectDocumentation`) wraps awkwardly | Use `Results.File(bytes, mime, fileName)`; record `(Content, MimeType, FileName)` flattens cleanly. |
| `RequireAuthorization()` interacts with antiforgery on POST | Antiforgery is enabled globally; `[ValidateAntiForgeryToken]` not needed because `app.UseAntiforgery()` validates non-safe requests automatically. React client always sends the token. Test it. |
| Existing Blazor `IMediator.Send` callers are unaffected | We don't touch handlers. Verify existing Blazor pages still work after each batch via spot-check (no automated harness for that). |

---

## Effort estimate

- Batch 1 (infra: JSON 401, exception middleware, harness scaffold): **3 days**
- Batch 2 (drift test infra, baseline TS commit): **2 days**
- Batch 3 (SignalR hub): **2 days**
- Batches 4–13 (handler wrapping, ~6 endpoints/day): **~10 days**
- Final acceptance gate, docs, behavioral diff: **1 day**

**Total: ~3.5 weeks** as a single PR. Realistically wider given review iterations.
