---
title: API Keys
description: Scoped, SHA256-hashed API keys for programmatic access, with Read/Execute scopes, optional project restriction, a mandatory expiry, and revocation tied to the owner's account.
---

API keys give programmatic clients — CI pipelines, monitoring bots, and MCP clients — scoped access to Beacon without an interactive login. Keys are SHA256-hashed at rest, carry explicit scopes, can be restricted to specific projects, always expire, and stop working when their owner's account is disabled or removed.

## Overview

Every API key is:

- **Scoped** — a key carries one or both scopes, `Read` and `Execute`, that limit what it can do
- **Hashed at rest** — only a SHA256 hash of the key is stored; the raw key is shown exactly once at creation
- **Bound to a user** — keys are created by a signed-in user and act for that user, never with more than that user may do; when the user is disabled or archived, the key stops working and is revoked
- **Revocable** — by its owner or by an administrator; revoking takes effect immediately on the next request
- **Expiring** — every new key has an expiration date: 90 days by default, at most `Beacon:ApiKeys:MaxLifetimeDays` (365 by default)
- **Optionally project-restricted** — confine a key to specific projects

The key format is:

```
sk-sem_<43 random characters>
```

Each key is generated from 32 bytes of cryptographically secure randomness.

## Creating a Key

1. Go to **API Keys** in the left navigation (`/api-keys`)
2. Click **Generate key**
3. Fill in the dialog:

| Field | Required | Description |
|-------|----------|-------------|
| **Key name** | Yes | A descriptive label, e.g. `CI Pipeline` |
| **Scopes** | Yes (at least one) | `Read` and/or `Execute` — see [Scopes](#scopes). `Execute` needs the Editor role or above |
| **Expiration date** | No | Leave empty for 90 days. A date further away than the configured maximum (365 days by default) is refused |

4. Click **Generate key** — the raw key is displayed once

:::caution
**The raw key is shown exactly once.** Beacon stores only a SHA256 hash — the plaintext key cannot be recovered later. Copy it immediately and store it in a secrets manager. If you lose it, revoke the key and generate a new one.
:::

The dialog blocks accidental dismissal (backdrop click and Escape are disabled) until you confirm you have copied the key.

**Who may create which key:**

- Any signed-in user with read access (Viewer and above) can create `Read` keys and revoke their own keys.
- `Execute` keys need write permission — the Editor role or above (or a super admin). A key never does more than its owner may: the `Execute` scope is checked against the owner's roles again on every request (see [Scopes](#scopes)).
- A disabled user cannot create keys.
- Keys are managed from a signed-in browser session only. A request authenticated with an API key, as an MCP caller or with a REST bearer token gets `403` on every `/beacon/api/api-keys` route — listing, creating and revoking keys, and the administrator routes.

### Project restrictions

Keys can optionally be restricted to specific projects via the `allowedProjectIds` field on the create request (`POST /beacon/api/api-keys`). A restricted key can only resolve and query the projects in its list — this is enforced fail-closed on the [MCP Server](/features/mcp-server/): if the restriction claim is missing or malformed, all project access is denied rather than falling open.

## Scopes

| Scope | Grants |
|-------|--------|
| `Read` | Read-only requests: `GET` routes under `/beacon/api` that list or read stored data — configs, reports, stored results |
| `Execute` | Everything else: every request that changes state, runs SQL, dials a data source, makes an outbound call or calls the LLM, and the MCP endpoint |

**How enforcement works:**

- For a key, every `/beacon/api` request that is not `GET`, `HEAD` or `OPTIONS` needs the `Execute` scope — creating or editing queries, subscriptions and recipients, running previews, evaluating data contracts, triggering scans, documentation generation, AI actors and so on. The few `GET` routes that run SQL or dial a data source (for example `GET /beacon/api/data-sources/{id}/metadata`, which scans the data source when no metadata is stored yet) need `Execute` too. Everything else a `Read` key may call is a read-only `GET`.
- This is enforced on every request, whether or not `Authorization.Enabled` or `UserManagement.Enabled` is set. When either is set, the owner's role is checked as well: the scope is a ceiling, not a grant.
- **`Execute` follows the owner.** A key keeps the `Execute` scope only while its owner has write permission — the Editor role or above, or super admin, and the authorization provider's consent — checked on every request from the owner's current roles and the provider's current answer. When the owner loses it, the key acts as a `Read` key (and a warning naming the key id is logged once per process); when the owner regains it, so does the key. This holds whatever `Authorization.Enabled` says.
- The **MCP endpoint** `/beacon/mcp` requires the `Execute` scope as a whole, because its tools can run SQL. A `Read` key cannot open an MCP session when the host maps the endpoint with `app.MapBeaconMcp()`, and inside the MCP layer every request from it is refused either way — see the [MCP Server guide](/features/mcp-server/).
- The **real-time hub** (`/beacon/api/hub`) accepts browser sessions only; no key reaches it.
- Routes that require the Admin role (user management, settings, data-source administration, the key administration below) are never reachable with a key: keys carry no role.
- Scopes are attached to the request as claims by the API-key authentication middleware. Interactive browser sessions (cookie/OIDC) carry no scope claims and are not scope-gated — they are governed by user roles instead. Scopes constrain **API keys and Entra callers on MCP only**.
- Requests authenticated with an API key or a bearer token in the `Authorization` header need no antiforgery token, and `GET /beacon/api/csrf` does not issue one to them (it answers `400`). Browser sessions keep sending the token as before.

:::note
Grant the narrowest scope that works. A monitoring integration that only reads configuration and reports needs `Read`; reserve `Execute` for clients that actually run queries or change things.
:::

## Managing Keys

The **API Keys** page (`/api-keys`) lists your keys with:

| Column | Description |
|--------|-------------|
| **Name** | The label you gave the key |
| **Prefix** | The first 16 characters (e.g. `sk-sem_AbCd12345…`) — enough to identify a key without exposing it |
| **Scopes** | The scopes the key grants (`Read`, `Execute`) |
| **Projects** | The projects the key is restricted to, or `—` when it is not restricted |
| **Created** | Creation timestamp |
| **Last used** | Timestamp of the most recent authenticated request, or `Never` |
| **Expires** | The expiration date, `Never` (keys created before expiry became mandatory), or an `Expired` badge |
| **Status** | `Active` while the key works; otherwise `Inactive` — revoked, expired, or its owner disabled or archived (the administrators' table says `Revoked` for a revoked key) |

### Revoking a key

Click **Revoke** next to an active key and confirm. Revocation is immediate — any integration using that key stops working on its next request. Revoked keys remain in the list (marked `Revoked`) for auditability; they are never silently deleted.

### Administering every user's keys

Administrators (the Admin role) see a second table on the same page, **All API keys**: every user's keys with their owner. Click an owner to show only that user's keys, and **Revoke** any active key. The same is available over the API:

| Method | Route | What it does |
|--------|-------|--------------|
| `GET` | `/beacon/api/api-keys/admin?userId={id}` | Every key, newest first, or only the keys of user `id`; paged and sortable like other lists |
| `DELETE` | `/beacon/api/api-keys/admin/{id}` | Revokes key `id`, whoever owns it |

Both require the Admin role, and an administrator in the user store at the time of the request (enabled; super admin or the Admin role) — a session whose user has since been demoted or disabled is refused with `403`. API keys never reach them.

### When the owner leaves

A key works only while its owner can sign in:

- **Disabling a user** (the toggle on the Users page, or saving the user as disabled) revokes all of that user's keys in the same save — and again on every later save of the user as disabled.
- **Enabling the user again does not bring any key back:** the re-enable itself revokes every key of theirs that is still active (for example a key created while they were being disabled, or a key of a user disabled before keys followed their owner). Issue new keys.
- **Archiving (deleting) a user** revokes their keys the same way.
- A key whose owner is disabled, archived or missing is rejected on every request, as is a key with no owner. Disabling, archiving and re-enabling also advance the owner's API-key generation, so a key created while one of those changes was being saved is rejected as well.

### Expiration

Every new key expires: after 90 days when no date is given, and never later than `Beacon:ApiKeys:MaxLifetimeDays` days after creation (365 by default; see [Configuration → API Keys](/getting-started/configuration/#api-keys)). A date that is not in the future, or beyond the maximum, is refused with `400`.

A key stops working at its expiry instant. An expired key fails validation exactly like a revoked one: the caller receives `401 Unauthorized` with an "Invalid or expired API key" problem response. Expired keys stay visible in the list with an `Expired` badge.

Keys created before expiry became mandatory may have no expiry. They keep working by default, and each logs a warning with its key id the first time it is used after a restart — rotate them. To retire them all at once, set `Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys` to `true`: such a key then expires `MaxLifetimeDays` after it was created (no stored data changes; unset the option and they work again).

## Using a Key

API keys are passed in the standard `Authorization` header as a Bearer token:

```
Authorization: Bearer sk-sem_YOUR_API_KEY
```

This is the **only** accepted format — there is no `X-Api-Key` header. The authentication middleware only engages when the header starts with `Bearer sk-sem_`; anything else falls through to the other authentication schemes (cookies, JWT).

### With the REST API

Read endpoints under `/beacon/api/*` accept a `Read` key directly; anything that changes state or runs SQL needs an `Execute` key. No antiforgery token is needed:

```bash
# List projects (Read)
curl "https://your-beacon-host/beacon/api/projects" \
  -H "Authorization: Bearer sk-sem_YOUR_API_KEY"

# List saved queries (Read)
curl "https://your-beacon-host/beacon/api/queries" \
  -H "Authorization: Bearer sk-sem_YOUR_API_KEY"
```

The full endpoint surface is described by the OpenAPI document at `/openapi/v1.json`. The `/beacon/api/api-keys` routes are the exception: keys are managed from a signed-in browser session only.

### With the MCP Server

API keys are the authentication mechanism for the [MCP Server](/features/mcp-server/) at `/beacon/mcp`. Use an `Execute` key and add it to your MCP client configuration:

```json
{
  "mcpServers": {
    "beacon": {
      "url": "https://your-beacon-host/beacon/mcp",
      "headers": {
        "Authorization": "Bearer sk-sem_YOUR_API_KEY"
      }
    }
  }
}
```

If the key is restricted to a single project, all MCP tools resolve that project automatically. If it has access to multiple projects, pass `project_id` in tool calls. See [MCP Server](/features/mcp-server/) for the full tool reference.

## How Validation Works

Understanding the mechanics helps when debugging authentication failures:

1. **Header check** — the middleware looks for `Authorization: Bearer sk-sem_...`. Requests already authenticated by another scheme (e.g. a browser cookie session) skip API-key processing entirely.
2. **Hash lookup** — the presented key is SHA256-hashed and matched against the stored hash. The plaintext key never touches the database; the stored 16-character prefix is for display in the UI only and plays no part in validation.
3. **Status checks** — a matching key is rejected if it has been revoked, if its expiry instant has been reached, or if its owner is missing, archived or disabled, or has moved to a later API-key generation than the key's; so is a key whose stored scopes leave no valid scope (none, or only unknown values). All of these return the same `401` problem response: "Invalid or expired API key." The reason is logged at Warning with the key id (`revoked`, `expired`, `owner_disabled`, `owner_archived`, `owner_missing`, `owner_generation_changed`, `no valid scopes`) — never the key or its hash.
4. **Bookkeeping** — the key's *Last used* timestamp is updated.
5. **Identity** — a claims identity is built from the key: its scopes (only `Read` and `Execute`; a key stored with the retired `Admin` scope gets `Execute`, and `Execute` reads as `Read` while the owner has no write permission by their roles or the authorization provider), its project restrictions, and the owner. The scope filter, authorization policies and — when enabled — the owner's role then decide per request.

## Security Best Practices

- **Use least scope.** Create separate keys per integration, each with only the scopes it needs. A read-only reporting job should never hold an `Execute` key.
- **Restrict to projects.** If an integration only works with one project, restrict the key to that project. A leaked restricted key exposes one project, not all of them.
- **Keep lifetimes short.** Prefer the shortest expiry that works; lower `Beacon:ApiKeys:MaxLifetimeDays` if your policy demands it. A forgotten key that expires is harmless.
- **Rotate regularly.** Generate a new key, switch the integration over, then revoke the old one. The *Last used* column tells you when the old key has actually gone quiet.
- **Never commit keys.** Keys must not appear in source code, config files under version control, CI logs, or chat. Inject them via environment variables or a secrets manager. If a key is ever committed — even to a private repository — revoke it immediately.
- **Revoke on suspicion.** Revocation is instant and free. If there is any doubt a key has been exposed, revoke first and re-issue after.
- **Audit periodically.** Administrators: review **All API keys** for keys with `Never` or stale *Last used* values, keys that never expire (created before expiry became mandatory), and keys of people who left, and revoke what is no longer needed.

:::note
Beacon never stores or logs the plaintext key. If you find yourself needing to "look up" an existing key's value, that is by design impossible — generate a new key instead.
:::

## Troubleshooting

**`401 Unauthorized` — "Invalid or expired API key"**
The key was recognized as an API key but failed validation. Check that it hasn't been revoked, hasn't passed its expiration date, that its owner's account is still enabled, and that it was copied in full (keys are long — a truncated paste is the most common cause). The server log names the key id and the reason.

**Request behaves as unauthenticated**
The middleware only engages on headers that begin with `Bearer sk-sem_`. Verify the header is exactly `Authorization: Bearer sk-sem_...` — a missing `Bearer ` prefix or a custom header name (e.g. `X-Api-Key`) is silently ignored.

**`403` — "This operation requires the Execute scope."**
The request changes state, runs SQL or calls out, and the key only has `Read` — or it has `Execute` but its owner no longer has the Editor role (or above). Generate a key with the `Execute` scope, or ask an administrator to restore the owner's role.

**`403` on `/beacon/api/api-keys`**
Keys cannot list, create or revoke keys, and neither can MCP callers or REST bearer tokens. Sign in to the UI.

**`403` when creating an `Execute` key**
Creating an `Execute` key needs the Editor role or above. Ask an administrator for the role, or create a `Read` key.

**`400` when creating a key**
The scope is not `Read` or `Execute` (the `Admin` scope can no longer be issued), no scope was given, or the expiration date is in the past or beyond the configured maximum.

**"No project found" via MCP**
The key's project restriction resolved to no accessible projects. Check the key's allowed projects, or create an unrestricted key.

## Upgrading from 4.5

API keys are now scoped on every route, expire, and follow their owner's account. What changes for hosts, integrations and users:

- **`Read` keys are read-only everywhere.** Every `/beacon/api` request other than `GET`/`HEAD`/`OPTIONS` — and `GET /beacon/api/data-sources/{id}/metadata` — answers `403` to a key without the `Execute` scope, independent of `Authorization.Enabled` and `UserManagement.Enabled`. The same applies to Entra callers on MCP mapped with the `Read` scope. Integrations that wrote with a `Read` key need an `Execute` key.
- **No antiforgery for header-authenticated callers.** Requests authenticated with an API key or a bearer token skip antiforgery validation, and `GET /beacon/api/csrf` answers them `400` instead of issuing a token. Clients that fetched a token before each mutation can drop that step. Browser sessions are unchanged.
- **The `Admin` scope is retired.** It never granted more than `Execute`. New keys can only carry `Read` and `Execute` (anything else is `400`); existing keys stored with `Admin` act as, and are listed as, `Execute`. The MCP discovery documents now advertise `scopes_supported: ["Execute"]`. A principal a host builds itself with a raw `Admin` scope claim no longer passes the Execute-scope policy.
- **Who may create keys.** `Execute` keys need write permission (Editor role or above); Viewers can now create `Read` keys and revoke their own keys. Disabled users cannot create keys (`403`).
- **Keys are managed from a browser session only.** Every `/beacon/api/api-keys` route — list, create, revoke, and the new administrator routes — answers `403` to a request authenticated with an API key, as an MCP caller or with a REST bearer token. Automation that listed or rotated keys with a key or a bearer token must move to a signed-in session. The handlers resolve the signed-in user from `NameIdentifier` (`Users.ExternalId`), as the authorization provider does.
- **`Execute` follows the owner's current write permission.** On every request, a key keeps `Execute` only while its owner is a super admin or holds the Editor role or above, and the configured `IBeaconAuthorizationProvider` grants them write permission — the rule keys are issued under. Otherwise it acts as a `Read` key on REST (including `GET /beacon/api/data-sources/{id}/metadata`) and on MCP. Existing `Execute` keys of Viewers (or of users without a role) therefore lose write access on upgrade — and regain it with the role. This applies whatever `Authorization.Enabled` says.
- **The authorization provider is asked about a key's owner on every request.** While `ApiKeyAuthMiddleware` asks `HasWritePermissionAsync` about an `Execute` key whose owner's roles allow writing, `HttpContext.User` is the owner as a signed-in session presents them: `NameIdentifier` = `Users.ExternalId`, name, email, display name and a role claim per stored role, authentication type `ApiKeyOwner` (`McpCallerClaimTypes.ApiKeyOwnerAuthenticationType`), no `auth_method` claim. The request's user is restored afterwards. A custom provider that resolves the user from `NameIdentifier` needs no change; one that denies keeps such keys at `Read`, and one that throws fails the request.
- **A key that grants nothing is refused.** A key whose stored scopes are empty, missing or only unknown values now gets `401` (it was authenticated with no scope before).
- **Every new key expires.** 90 days by default, at most `Beacon:ApiKeys:MaxLifetimeDays` (new setting, default `365`, range 1–3650, validated at startup); a key stops working at its expiry instant. Existing keys keep their stored expiry — including keys that never expire: each logs a warning with its id once per process when used. Rotate them, or set `Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys` (new, default `false`) to expire them `MaxLifetimeDays` after creation.
- **Keys follow their owner.** A key whose owner is disabled, archived or missing — or that has no owner — is rejected. Disabling or archiving a user revokes all of their keys in the same save, and re-enabling a user revokes every key of theirs still active, so no key outlives a disable — including keys of users disabled before this version. Each of these changes also advances the user's API-key generation in that save, and a key is accepted only while it carries its owner's current generation, so a key approved before the change and stored after it — one the revocation could not see — is rejected too (`owner_generation_changed`). Validation refusals are logged at Warning with the key id and reason.
- **Replaced `IApiKeyService`.** `GenerateApiKeyAsync(int userId, string name, string[] scopes, int[]? allowedProjectIds = null, DateTime? expiresAt = null, int ownerGeneration = 0, CancellationToken ct = default)`: the user id and scopes are now required (non-nullable); it validates scopes and expiry and throws `InvalidOperationException` otherwise, and stores `ownerGeneration` (the owner's `ApiKeyGeneration` read with the checks that approved the key) as the key's `OwnerGeneration`. `ValidateApiKeyAsync` must refuse a key whose `OwnerGeneration` is not its owner's `ApiKeyGeneration`, and must return the credential with `UserId` set and the `User` navigation loaded, including `User.UserRoles` with their `Role` — `ApiKeyAuthMiddleware` refuses a credential without its user (`401`) and withholds `Execute` from a key whose user's roles are not loaded.
- **Schema: two new columns.** The `AddApiKeyOwnerGeneration` migration (PostgreSQL `20261010090000`, SQL Server `20261010090001`) adds the user's API-key generation (`beacon.users.api_key_generation`; `[beacon].[Users].[ApiKeyGeneration]` on SQL Server) and the generation each key was issued in (`beacon.api_key_credentials.owner_generation`; `[beacon].[ApiKeyCredentials].[OwnerGeneration]`), both `int NOT NULL DEFAULT 0`. It only adds columns and rewrites no data: every existing key and user start at generation 0, so existing keys keep working.
- **Key listings.** `GET /beacon/api/api-keys` entries now return the scopes correctly and include `allowedProjectIds`; `isActive` is now false for an expired key, for a key whose owner is disabled or archived, and for a key no longer in its owner's API-key generation, not only for a revoked one. In C#, `ApiKeyEntry` gains `AllowedProjectIds` as its last, optional positional parameter (code that constructs it positionally needs a recompile). New Admin-role routes: `GET /beacon/api/api-keys/admin` and `DELETE /beacon/api/api-keys/admin/{id}` (the typed client gains `getAllApiKeys` and `adminRevokeApiKey`); an administrator's revocation is logged with their user id and the key id. Besides the session's Admin role claim, both check the user store on every request: the caller must be enabled and a super admin or hold the Admin role now. A session signed in before its user was demoted or disabled still carries the Admin claim and gets `403`.
- **Map MCP with `app.MapBeaconMcp()`.** It maps `/beacon/mcp` behind the Execute-scope policy and replaces `app.MapMcp("/beacon/mcp").RequireAuthorization(...)`. It needs the policies from `builder.Services.AddBeaconApiAuthorization()`. Even on a weaker mapping, the MCP layer now refuses a key or Entra caller without the `Execute` scope, and over HTTP a caller that is not authenticated: tool calls answer a tool error (no tool runs, and the refusal is audited like any tool call), every other request but `initialize` and `ping` a JSON-RPC error.
- **SSO sessions never carry scoped-caller claims.** Sign-in now removes every `auth_method`, `scope`, `allowed_projects`, `api_key_id`, `api_key_name`, `caller_kind` and `caller_hash` claim the identity provider sent, and MCP reads a project restriction and a key id only from an API-key or mapped MCP caller principal.
- **Policy name.** The Execute-scope policy name now lives in Core as `Beacon.Core.Authorization.BeaconScopes.ExecuteScopePolicyName`; `BeaconApiEndpoints.ExecuteScopePolicyName` remains and has the same value.
