# React Migration — Phase 1 Batches 1–3 Behavioral Diff

**Date:** 2026-05-07
**Branch:** `feature/react-migration-phase1`
**Spec:** `ClaudePlans/ReactMigration-Phase1.md`
**Scope of this diff:** Infrastructure only (Batches 1, 2, 3). Batches 4–13 (handler wrapping) deferred to subsequent sessions.

## What changed

### Batch 1 — JSON-401 + exception middleware

| File | Change |
|---|---|
| `src/Beacon.UI/ServiceExtensions.cs` | `AddBeaconCookieAuthentication` now configures `Events.OnRedirectToLogin` and `Events.OnRedirectToAccessDenied` to emit `application/problem+json` 401/403 for any request whose path starts with `/beacon/api/` or `/beacon/mcp` or accepts `application/json`. Browser navigations under Blazor still get HTML redirects. |
| `src/Beacon.SampleProject/Middleware/ApiExceptionMiddleware.cs` | New. Maps `InvalidOperationException` → 400, `BeaconException` → 400, `UnauthorizedAccessException` → 403, anything else → 500 (logged with context, generic message returned per §1.11). Emits RFC 7807 `application/problem+json`. Scoped via `app.UseApiExceptionHandler("/beacon/api")`. |
| `src/Beacon.SampleProject/Endpoints/BeaconApiEndpoints.cs` | New `AddBeaconApiAuthorization()` registers a named auth policy (`BeaconApi`) bound to the cookie scheme. `MapBeaconApi()` applies the policy at the route group; individual endpoints opt out via `.AllowAnonymous()`. |
| `src/Beacon.SampleProject/Endpoints/AuthEndpoints.cs` | Removed explicit `.RequireAuthorization()` from `/auth/permissions` — group-level policy handles it. |
| `src/Beacon.SampleProject/Program.cs` | Added `AddBeaconApiAuthorization()` and `app.UseApiExceptionHandler("/beacon/api")`. |

**Effect:** The Phase 0 caveat is gone. Anonymous `GET /beacon/api/auth/permissions` returns:

```
HTTP/1.1 401 Unauthorized
Content-Type: application/problem+json
{"type":"about:blank","title":"Unauthorized","status":401}
```

instead of a 500 with HTML stack trace. Same shape will apply to every authenticated handler endpoint added in batches 4–10.

### Batch 2 — Integration test harness

| File | Change |
|---|---|
| `src/Beacon.Tests/Beacon.Tests.csproj` | Added `Microsoft.AspNetCore.Mvc.Testing@9.0.11`. Added project reference to `Beacon.SampleProject`. |
| `src/Beacon.SampleProject/Program.cs` | Added `public partial class Program;` shim so `WebApplicationFactory<Program>` compiles. |
| `src/Beacon.Tests/Integration/Api/BeaconWebApplicationFactory.cs` | New. Inherits `WebApplicationFactory<Program>`. Reads optional `BEACON_TEST_CONNECTION_STRING` env var to override the dev DB. Otherwise reuses dev config. |
| `src/Beacon.Tests/Integration/Api/Phase1HarnessTests.cs` | New. Six NUnit tests under `[Category("Phase1Harness")]`: `Health_Anonymous_Returns200`, `AuthMe_Anonymous_ReturnsIsAuthenticatedFalse`, `AuthPermissions_Anonymous_Returns401Json`, `Csrf_Anonymous_ReturnsTokenAndSetsCookie`, `UnknownApiPath_Returns404`, `Hub_Anonymous_Returns401Json`. Falls back to `Assert.Inconclusive(...)` if the host can't bootstrap (no DB). |

**Effect:** First end-to-end automated test coverage of the new HTTP surface. Future endpoint batches add more `[Category("Phase1Endpoints")]` tests using the same harness. Local runs use the dev DB; CI sets `BEACON_TEST_CONNECTION_STRING`.

### Batch 3 — SignalR hub skeleton

| File | Change |
|---|---|
| `src/Beacon.SampleProject/Hubs/BeaconHub.cs` | New. `[Authorize]` hub bound to the cookie scheme. Defines event-name constants and three event records (`JobStatusChangedEvent`, `NotificationCreatedEvent`, `ApprovalUpdatedEvent`). |
| `src/Beacon.SampleProject/SignalR/HubUserIdProvider.cs` | New. `IUserIdProvider` resolving `ClaimTypes.NameIdentifier`. |
| `src/Beacon.SampleProject/SignalR/HangfireSignalRJobFilter.cs` | New. Hangfire `IApplyStateFilter` that reads job parameter `BeaconUserId` and, if present, publishes `JobStatusChanged` to that user via `IHubContext<BeaconHub>`. Jobs without the parameter publish nothing — no broadcast (PII-safe per §1.11). |
| `src/Beacon.SampleProject/Program.cs` | `AddSignalR()` + `AddSingleton<IUserIdProvider, HubUserIdProvider>()` + `AddSingleton<HangfireSignalRJobFilter>()` + `app.MapHub<BeaconHub>("/beacon/api/hub").RequireAuthorization(...)` + `Hangfire.GlobalJobFilters.Filters.Add(...)`. |
| `src/Beacon.SampleProject/web/package.json` | Added `@microsoft/signalr@^8.0.7`. |
| `src/Beacon.SampleProject/web/src/lib/hub.ts` | New. `connectBeaconHub()` returns a typed `BeaconHub` exposing `onJobStatusChanged`, `onNotificationCreated`, `onApprovalUpdated`, and `stop`. |

**Effect:** The hub is wired and authorized but **no publishers exist yet for `NotificationCreated` or `ApprovalUpdated`**. They land in their feature-area batches (Batch 6 publishes from `ApproveQueryChange`/`RejectQueryChange`; `NotificationCreated` lives in `INotificationService` which isn't a MediatR handler, so its publisher comes alongside Phase 3 page work). The Hangfire filter is wired but only fires when a future enqueueing site sets `BeaconUserId` — current Hangfire jobs (recurring MCP learning aggregations) deliberately have no user, so they don't publish.

## What did NOT change

- No MediatR handler touched.
- No Blazor page or component touched.
- No DB schema, migration, or entity touched.
- No connector touched.
- Existing `src/Beacon.UI/Authentication/LoginEndpoints.cs` and `SetupEndpoints.cs` — untouched.
- No CORS configuration. Same-origin only (D1).
- No bearer JWT for API. Cookie auth only.

## Smoke test (HTTP, port 5299)

| Path | Before | After |
|---|---|---|
| `GET /beacon/api/health` (anon) | 200 `{"status":"ok"}` | 200 `{"status":"ok"}` (unchanged) |
| `GET /beacon/api/auth/me` (anon) | 200 `{"isAuthenticated":false}` | 200 `{"isAuthenticated":false}` (unchanged) |
| `GET /beacon/api/auth/permissions` (anon) | **500 text/plain** (Phase 0 caveat) | **401 application/problem+json** ✅ |
| `GET /beacon/api/csrf` (anon) | 200 `{"token":"..."}` | 200 `{"token":"..."}` (unchanged) |
| `POST /beacon/api/hub/negotiate?...` (anon) | n/a | 401 application/problem+json ✅ |
| `GET /beacon` (Blazor login) | 200 HTML | 200 HTML (unchanged) |
| `GET /beacon/mcp` (anon, was 401 before) | 401 | 401 (unchanged) |
| `dotnet test` | 28 pass | **34 pass** (28 + 6 new) |
| `dotnet build -c Release` | green | green |
| `npm run build` | green | green |

## Known caveats / deferred to later batches

1. **No `NotificationCreated` publisher.** `INotificationService` is not a MediatR handler — Phase 3 will add the publisher when the related pages migrate.
2. **No `ApprovalUpdated` publisher.** Lands in Batch 6 alongside the approvals endpoints.
3. **No exception middleware unit tests.** Only integration-level coverage so far. Consider `ProblemDetails` integration tests in Batch 4 when the first throwing handler is wrapped.
4. **`HangfireSignalRJobFilter` is wired but inactive** until a Hangfire-enqueueing handler sets `BeaconUserId`. Batch 4's `GenerateProjectDocumentationHandler` is the first such consumer.
5. **`BeaconException` doesn't carry a status code today.** Middleware always maps it to 400. If a future need arises, add `StatusCode` to the base class and update the mapper.
6. **No NSwag drift check or OpenAPI contract test yet.** Those land in Batch 11. Phase 0's NSwag config is set up; the generated TS file is still gitignored.

## Phase 1 progress

- [x] Batch 1 — JSON-401 + exception middleware
- [x] Batch 2 — Integration test harness
- [x] Batch 3 — SignalR hub skeleton
- [ ] Batch 4 — Projects (8 endpoints) + first Hangfire SignalR consumer
- [ ] Batch 5 — QueryFolders + Queries + QueryVersions (11 endpoints)
- [ ] Batch 6 — Approvals + ApiKeys (7 endpoints) + ApprovalUpdated publisher
- [ ] Batch 7 — Dashboards (11 endpoints)
- [ ] Batch 8 — DataQuality + DataCatalog (8 endpoints)
- [ ] Batch 9 — McpSettings + McpLearning (7 endpoints)
- [ ] Batch 10 — AiActors (11 endpoints) — resolve Core/AI handler ambiguity
- [ ] Batch 11 — OpenAPI contract test + NSwag drift test + commit baseline TS
- [ ] Batch 12 — React generated-client wrapper, replace `fetchJson`
- [ ] Batch 13 — Final acceptance gate, behavioral diff, CLAUDE.md update
