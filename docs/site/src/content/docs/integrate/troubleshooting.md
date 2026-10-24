---
title: Troubleshooting
description: "Startup failures, empty pages, 401s and 404s, jobs that do nothing, MCP clients that cannot connect: symptoms, causes and fixes for an embedded Beacon."
---

## The host does not start

| Message (or symptom) | Cause | Fix |
|---|---|---|
| `Implementation of IBeaconScheduler is required.` | No scheduler registered. | `options.AddBeaconScheduler<T>()`. → [Background jobs](/integrate/background-jobs/) |
| `CS9137` (interceptors) at build | `AddOpenApi`/`MapOpenApi` without a direct `Microsoft.AspNetCore.OpenApi` reference. | Add the package to your project, or drop the two calls. |
| `CS0122 'RoleService' is inaccessible` | A claims transformation copied from the reference host uses Beacon's internal role constants. | Use the version on [Host wiring](/integrate/host-setup/#the-claims-transformation). |
| An `InvalidOperationException` about `Beacon:EncryptionKey` | Key missing, too short, not random, a published example, or a `${…}` placeholder. | `openssl rand -base64 32` into a secret store / `Beacon__EncryptionKey`. |
| `Connection string 'BeaconContext' is not configured` | `UsePostgreSql()`/`UseSqlServer()` without `ConnectionStrings:BeaconContext`. | Set it, or pass a connection string. In development check that user-secrets load (they only load in `Development`). |
| An error about `Beacon:Schema` | The key is set to something other than `beacon`. | Remove it. The schema is fixed. |
| `Beacon:DataProtection:ApplicationName must be set outside Development` | `AddBeaconDataProtection` outside `Development` without an application name. | Set it, or run with `ASPNETCORE_ENVIRONMENT=Development` locally. |
| Data Protection keys "persisted without encryption at rest" | Outside `Development` the key ring has no protector. | Chain `ProtectKeysWithCertificate(...)` / `ProtectKeysWithAzureKeyVault(...)`, or set `Beacon:DataProtection:AllowUnencryptedKeys` deliberately. |
| `Beacon:UserManagement:SetupToken must be at least 32 characters` | Short setup token. | Use a longer one or unset it. |
| Local login off and "nobody could sign in" | `LocalLogin:Enabled=false` without SSO, external login or a break-glass name. | Enable OIDC, configure `ExternalLogin`, or add a break-glass name. |
| OIDC: missing Authority/ClientId/ClientSecret or admission rule | `Oidc:Enabled` without required values or without `AllowedTenants` (or `AllowAnyTenant`). | Complete the section. |
| `Beacon:Mcp:Callers` validation errors | Callers configured without `AllowedTenants`, without bearer JWT validation, or with both `ClientId` and `ObjectId`. | See [Connect AI clients](/mcp/connect/#option-b-entra-id-callers). |
| `Beacon host endpoint tools are misconfigured:` | A `[BeaconTool]` endpoint without `ReadOnly`, without authorization metadata, with an untrusted auth scheme, or a write tool without `AllowWriteTools`. | Fix each listed endpoint. |
| `AddHostEndpointTools can only be called once.` | Called twice. | Call it once, after `AddBeaconMcp()`. |
| `The MCP tool name '…' is claimed by both …` | Two tool sources (saved-query tools, host endpoint tools, an attribute tool the host added) serve the same name or name prefix. | Rename the host's tool so it no longer starts with `q_` or `api_` or reuses a catalog name. |
| Options validation for `Beacon:Query`, `Beacon:Mcp`, `Beacon:ApiKeys`, `Beacon:Notifications`, `Beacon:Authorization`… | A value is out of range or malformed. | The message names the key; see [Configuration](/getting-started/configuration/). |

## Database

| Symptom | Cause | Fix |
|---|---|---|
| `3D000: database "…" does not exist` | The database itself is missing; `UseBeacon` creates the schema, not the database. | `CREATE DATABASE beacon;` (or `POSTGRES_DB=beacon` on the container). |
| `relation "…" does not exist` / `Invalid object name 'beacon.…'` | Beacon's schema was never migrated. | Call `Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true)` at startup. → [Metadata database](/integrate/database/) |
| `extension "vector" is not available` | pgvector is not installed for this PostgreSQL server. | Install pgvector (Azure: allow `VECTOR` in `azure.extensions`), or use an image that has it (e.g. `pgvector/pgvector`). |
| `permission denied to create extension "vector"` | The migrating login cannot create extensions. | Create it once as an administrator: `CREATE EXTENSION vector;`. |
| `type "vector" does not exist` with your own `NpgsqlDataSource` | The extension lives in a schema that is not on the search path. | Install it in `public` (Beacon's search path is `beacon, public`). |

## The UI

| Symptom | Cause | Fix |
|---|---|---|
| 404 at `/` | The UI lives at `/beacon`. | Open `/beacon`, or redirect `/` to it yourself. |
| 404 for `/beacon` or blank page; asset 404s | `MapBeaconUi()` or `UseStaticFiles()` missing; or, building from source, the React app was not built (`Debug` builds skip it). | Add both calls; build `Release`, or `-p:BuildReactInDebug=true`. |
| Endless redirect to `/login` (not `/beacon/login`) | `AddBeaconCookieAuthentication("/")`. | Pass `"/beacon"`. |
| Always redirected to `/beacon/setup` (the health check and the MCP discovery documents still answer) | User management on and no administrator yet. | Complete first-run setup with the setup token (printed once to the console, or `Beacon:UserManagement:SetupToken`). |
| Signed in, but every API call answers 401 | Middleware order: `BeaconCookieAuthMiddleware` must run before `UseAuthorization`. | Follow the [pipeline order](/integrate/host-setup/#the-middleware-pipeline). |
| Saves fail with 400 "antiforgery" | No `AddAntiforgery`/`UseAntiforgery`, or a different header name without the SPA header. | Configure antiforgery with `HeaderName = "X-XSRF-TOKEN"`. |
| An error shows as "Internal server error" in the UI | Business-rule exceptions are not mapped to problem+json. | Add the [error handler](/integrate/host-setup/#error-responses). |
| A project, query or data source is missing for a user | Project access: the project is restricted and they are not a member, or the data source is not linked to a project they can read. | Add them as a member or link the data source. Restricted resources answer 404 on purpose. |
| No live updates | Realtime off, or `AddBeaconApiServices()` missing, or a proxy blocks WebSockets on `/beacon/api/hub`. | Check `realtimeEnabled` in `GET /beacon/api/auth/me`. |

## Scheduled checks

| Symptom | Cause | Fix |
|---|---|---|
| Subscriptions never run | Your scheduler does not register the recurring job, or no worker is running. | Check `AddOrUpdate` and your runner's dashboard. |
| Jobs run but do nothing; log warns about a scope without project access | The job's scope was not marked as system work. | Call `ISystemWorkScope.MarkAsSystemWork()` first in every job. |
| Run recorded as **Skipped** | The subscription's owner can no longer run it (removed from the project, role lowered, disabled). | Give it a new owner (UserAdmin or Admin) or restore access. |
| Run refused: "not approved" | Approval workflow on and the query's current version is not approved. | An Approver approves the change. |
| `NotImplementedException: … not implemented in this host` | The feature calls a scheduler member you left at its default. | Implement `AddOrUpdateDataQualityJob`, `EnqueueProjectDocumentation`, `EnqueueAiActorThinkCycle` or `EnqueueMcpEval`. |
| Notification links 404 | `BaseUrl` lacks `/beacon`. | Set it to `https://host/beacon`. |
| A recipient receives nothing | Destination not allowed by `Beacon:Notifications`, or awaiting approval. | Check the startup policy log line and **Approvals**. |

## MCP

| Symptom | Cause | Fix |
|---|---|---|
| 401 | No or invalid key/token. | Send `Authorization: Bearer sk-sem_…`. |
| 403 | Read-scoped key, an unmapped JWT caller, or a signed-in browser session. | Use an Execute key or map the caller; browsers use the MCP playground. |
| "No projects are available to you." | The key names no reachable project. | Create the key with a project. |
| "Project access could not be determined; retry the call." | A transient failure while resolving access. | Retry; check the database. |
| Discovery documents show the wrong host | `Beacon:PublicBaseUrl` unset behind a proxy. | Set it. |
| Calls fail randomly across instances | Legacy MCP sessions without affinity. | Enable session affinity for `/beacon/mcp`. |

Still stuck? Compare your `Program.cs` with `src/Beacon.SampleProject/Program.cs`, and [open an issue](https://github.com/moberghr/beacon/issues).
