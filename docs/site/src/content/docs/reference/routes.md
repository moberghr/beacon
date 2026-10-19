---
title: Routes & endpoints
description: "Every URL Beacon serves: UI pages under /beacon, anonymous routes, REST API groups under /beacon/api, the SignalR hub, the MCP server and discovery documents, with the paging contract and error responses."
---

Beacon serves everything under `/beacon`. The site root `/` belongs to the host application. Paths below are absolute; replace the origin with your host's.

| Prefix | What |
|---|---|
| `/beacon` | The React UI (`app.MapBeaconUi()`). |
| `/beacon/api` | REST API and SignalR hub (`app.MapBeaconApi()`, `MapLoginEndpoints`, `MapSetupEndpoints`). |
| `/beacon/mcp` | MCP server (`app.MapBeaconMcp()`). |
| `/.well-known/…` | MCP discovery documents (`app.MapMcpDiscovery()`). |

## UI pages

The UI is a single-page app with the router mounted at `/beacon`. Every path below is under `/beacon`, for example `/beacon/queries`. Signed-out visitors are sent to `/beacon/login`. On a new installation, open `/beacon/setup` to create the first admin.

"Project access" means a user with a Viewer or Editor role, or an Admin. A user who holds only functional permissions does not see those pages. The UI hides what a user cannot use; the API enforces it.

| Path | Page | Who sees it |
|---|---|---|
| `/login`, `/logout` | Sign in, sign out | Anyone |
| `/setup` | First-run setup: create the super admin | Anyone, while no admin exists |
| `/error` | Error page | Anyone |
| `/`, `/home` | Home: KPIs, trends, activity, uptime | Everyone signed in |
| `/about` | About Beacon | Everyone signed in |
| `/settings` | Your settings and password | Everyone signed in |
| `/control-tower` | Control Tower: health of subscriptions and contracts | Project access |
| `/data-sources`, `/data-sources/:id` | Data sources list and detail | List: project access or DataSourceAdmin. Detail: project access |
| `/data-sources/:id/relationships` | Schema relationships and schema health | Project access |
| `/projects`, `/projects/new`, `/projects/:id` | Projects: members, linked data sources, documentation | Project access |
| `/data-quality`, `/data-quality/:id` | Data contracts overview and detail | Project access |
| `/migration-jobs`, `/migration-jobs/new`, `/migration-jobs/:id`, `/migration-history` | Data migration | MigrationOperator or Admin, only when `Beacon:Migrations:Enabled` is on |
| `/ai-actors`, `/ai-actors/:id`, `/ai-actors/:id/edit` | AI actors | Project access |
| `/queries`, `/queries/new`, `/queries/:id`, `/queries/:id/edit` | Queries list, new query, detail, editor | Project access |
| `/queries/:id/versions`, `/queries/:id/versions/:versionId` | Query versions | Project access |
| `/subscriptions`, `/subscriptions/:id` | Subscriptions list and detail | Project access |
| `/notifications`, `/notifications/:id` | Notification history and detail | Project access |
| `/recipients` | Recipients | Project access (changes: SecurityAdmin or Admin) |
| `/tasks`, `/tasks/:id` | Tasks | Project access |
| `/api-keys` | API keys | Project access, Auditor or UserAdmin |
| `/mcp-settings` | MCP guardrail settings | Auditor (read) or SecurityAdmin |
| `/mcp-playground` | Try the MCP tools in the browser | Project access |
| `/mcp-learning` | Learned patterns and documentation patches | Project access |
| `/approvals`, `/approvals/:id`, `/approvals/changes/:id` | Approvals of query changes and other changes | Approver or Admin |
| `/users` | User management | UserAdmin or Admin |
| `/admin-settings` | Admin settings, LLM provider | Auditor (read) or SecurityAdmin |

Any other path under `/beacon` redirects to `/beacon/home`.

## Anonymous routes

These answer without a session. Everything else requires one, an API key or a bearer token.

| Method and path | What | Notes |
|---|---|---|
| `GET /beacon/api/health` | Health check | `200 {"status":"ok"}` when the metadata database answers within 5 seconds, otherwise `503` problem. |
| `GET /beacon/api/auth/me` | The current session, if any | |
| `GET /beacon/api/auth/sso` | Whether SSO is offered, for the login page | |
| `GET /beacon/api/csrf` | Antiforgery token for the UI | `400` for callers authenticated by an `Authorization` header. |
| `POST /beacon/api/auth/login` | Password sign-in | Mapped only while password sign-in is on or a break-glass account is configured. 10 attempts per minute per client IP, then `429` with `Retry-After`. |
| `POST /beacon/api/auth/logout` | Sign out; `?everywhere=true` signs out every session of the user | |
| `GET /beacon/api/auth/sso/challenge` | Start the OIDC sign-in | |
| `GET /beacon/api/setup/status` | Whether first-run setup is open | Mapped when user management is on. |
| `POST /beacon/api/setup/superadmin` | Create the super admin with the setup token | Throttled per IP. `403` wrong token, `409` lost a race, `400` already set up. |
| `GET /beacon/api/setup/roles` | Roles, for the setup page | Anonymous only while setup is open. |
| `GET /.well-known/oauth-protected-resource` | MCP protected-resource metadata (RFC 9728) | Also at `/.well-known/oauth-protected-resource/beacon/mcp`. |
| `GET /.well-known/mcp/server-card.json` | MCP server card | |
| `/signin-oidc` | OIDC callback | When OIDC is enabled. Path set by `CallbackPath`. |
| `GET /signout-oidc` | Front-channel sign-out | Only with `EnableFrontChannelLogout`. |

Until the first admin exists, the anonymous API routes above, the health check included, answer with a redirect to `/beacon/setup`. Only the setup routes and the MCP discovery documents answer normally.

## REST API

All groups are under `/beacon/api` and need a signed-in session, an API key (`Authorization: Bearer sk-sem_…`) or a bearer JWT. For callers with a scope (API keys, mapped MCP callers), every request other than `GET`, `HEAD` or `OPTIONS`, and a few marked reads, need the **Execute** scope. Cookie sessions send the antiforgery header `X-XSRF-TOKEN` on state-changing requests.

Functional permissions named below are held by Admins too. A by-id request on a resource in a project you cannot read answers `404`; one you can read but may not act on answers `403`.

| Prefix | Covers | Notable rules |
|---|---|---|
| `/auth/me`, `/auth/sso`, `/auth/permissions` | Current user, SSO config, current permissions | `permissions` returns `canRead`, `canWrite`, `isAdmin`, `permissions`, `migrationsEnabled`. |
| `/home` | Home page: trends, activity, task summary, uptime, migration summary | `migration-summary` admits MigrationOperator. |
| `/projects` | Projects, linked data sources, members, access mode, repositories, documentation, imported documents, MCP context | Members: project admins, UserAdmin. Access mode: UserAdmin (opening a restricted project: Admin). Unlinking a data source: Admin. Repository token: SecurityAdmin. |
| `/data-sources` | Data sources, connection test, metadata, metadata refresh | Create, test and delete: DataSourceAdmin. `GET /{id}/metadata` needs Execute scope. `PUT /{id}/migration-writes`: Admin. Forced refresh is rate limited per data source (`429`). |
| `/data-sources/{id}/relationships`, `/data-sources/{id}/schema-health` | Schema relationships: list, add, edit, verify, delete, discover; schema health | Verifying needs the Curate action. |
| `/queries` | Queries: list, detail, create, update, lock, change history, preview, step preview, publish as MCP tool | No delete route. Previews need Execute scope. `PUT /{id}/mcp-tool`: SecurityAdmin. |
| `/queries/{id}/versions`, `/query-versions` | Versions: list, detail, diff, restore | |
| `/query-folders`, `/queries/{id}/folder` | Query folders and moving a query between them | |
| `/subscriptions` | Subscriptions: list, create, detail, delete, reactivate, SLA, test run, recipients, anomaly chart, owner | Changes need Execute scope. `PUT /{id}/owner`: UserAdmin. No route changes the schedule. |
| `/recipients` | Recipients | Create, update, delete: SecurityAdmin. |
| `/notifications` | Notification history and detail | Filtered to the caller's projects. |
| `/tasks` | Tasks: list, detail, resolve, assign, snooze, priority, watch, comments, executions, result history | |
| `/control-tower` | Statistics, health grid, subscription detail | Filtered to the caller's projects. |
| `/data-quality` | Overview, data contracts, evaluations, evaluate now, owner | Changes need Execute scope. `PUT /contracts/{id}/owner`: UserAdmin. Custom SQL rules: Admin. |
| `/approvals` | Pending query approvals, change approvals (`/approvals/changes`), approve, reject | Reading admits Approver; approve and reject require Approver. |
| `/migrations` | Migration jobs and executions | Mapped only when `Beacon:Migrations:Enabled` is on. MigrationOperator. |
| `/ai-actors` | AI actors, think, pause, resume, refine, plans | `PUT /{id}/owner`: UserAdmin. |
| `/mcp` | MCP settings and locks, project settings, learned patterns, documentation patches, learning stats, playground tools, audit log | Settings: read Auditor or SecurityAdmin, write SecurityAdmin. `POST /tools/run` needs Execute scope. `GET /audit`: Auditor. |
| `/eval` | Golden examples, candidates, feedback, grounding re-check, eval runs | Admin. |
| `/glossary` | Business glossary | Admin. |
| `/api-keys` | Your API keys; `/api-keys/admin` for all keys | Create from a signed-in session only. `GET /admin`: Auditor or UserAdmin. `DELETE /admin/{id}`: UserAdmin. |
| `/users` | Users, roles, enable/disable, sign out everywhere | UserAdmin. |
| `/user-settings` | Your settings, change your password | |
| `/admin-settings` | Runtime settings, LLM test | Read: Auditor or SecurityAdmin. Write and test: SecurityAdmin (LLM connection and base URL: Admin). |

The OpenAPI document at `/openapi/v1.json` lists every operation with its schema (see [Host-mapped routes](#host-mapped-routes)).

### Paging, sorting and filtering

Every list endpoint takes the same query parameters:

| Parameter | Default | Rules |
|---|---|---|
| `page` | `0` | Zero-based. Negative values count as `0`. |
| `pageSize` | `20` | Clamped to 1–200, never rejected. |
| `sort` | the list's own order | Comma-separated property names, `-` for descending, for example `sort=-createdTime,name`. Unknown names are ignored. |

A paged response is `{ "items": [...], "totalCount": n, "pageCount": n }`. Lists are filtered to the projects the caller can read in SQL, before paging, so counts never include rows you cannot see.

### Errors

| Situation | Response |
|---|---|
| No session, key or token on an API route | `401`, `application/problem+json`: `{"type":"about:blank","title":"Unauthorized","status":401}`. API routes never redirect to the login page. |
| Signed in, but the role, scope or permission does not allow the action | `403` problem. |
| A resource that does not exist, or that is in a project you cannot read | `404` problem. Beacon does not reveal whether it exists. |
| Readable, but the action is not granted in that project | `403` problem. |
| A conflicting change (a stale approval hash, a membership race, a locked setting) | `409` problem. A locked setting uses type `/errors/setting-locked`. |
| Too many requests (sign-in, setup, forced metadata refresh) | `429`. |

The sample host also maps unhandled exceptions under `/beacon/api` to problem responses: `400` for business-rule errors (types `/errors/invalid-operation`, `/errors/beacon`) and `500` (`/errors/internal`) otherwise. That mapping is sample code; an embedding host supplies its own.

## Realtime hub

| Path | Protocol | Who |
|---|---|---|
| `/beacon/api/hub` | SignalR | Signed-in users who can read project data, or who hold a functional permission whose pages use hub events; others get `403`. The connection is closed when the session's next check is due, and the client reconnects. |

Events are sent only to the user they concern:

| Event | Payload | When |
|---|---|---|
| `JobStatusChanged` | `jobId`, `state`, `timestamp` | A background job the user started changed state. Published by the host's job runner (the sample's Warp pipeline does). |
| `ApprovalUpdated` | `approvalId`, `status` | An approval request was approved or rejected. |

A host turns the hub off with `AddBeaconApiServices(x => x.Realtime = false)`.

## MCP server

| Path | What |
|---|---|
| `/beacon/mcp` | MCP over Streamable HTTP. Needs the Execute scope: an API key (`Authorization: Bearer sk-sem_…`) or a mapped Entra bearer token. A `401` carries `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"`. |

The discovery documents are listed under [Anonymous routes](#anonymous-routes). Set `Beacon:PublicBaseUrl` so they advertise your public origin. See [MCP server](/features/mcp-server/).

## Host-mapped routes

These come from the host, not from the Beacon packages. The sample host maps them; your host decides for itself.

| Path | What |
|---|---|
| `/openapi/v1.json` | OpenAPI document of the REST API (`app.MapOpenApi()`). The UI's typed client is generated from it. |
| `/warp` | Warp job dashboard, sample host only. Signed-in Admins. |
