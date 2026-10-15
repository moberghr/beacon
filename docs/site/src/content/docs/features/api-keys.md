---
title: API Keys
description: Scoped, SHA256-hashed API keys for programmatic access, with Read/Execute scopes, a project list, a mandatory expiry, and revocation tied to the owner's account.
---

API keys give programmatic clients — CI pipelines, monitoring bots, and MCP clients — scoped access to Beacon without an interactive login. Keys are SHA256-hashed at rest, carry explicit scopes, are confined to the projects they name, always expire, and stop working when their owner's account is disabled or removed.

## Overview

Every API key is:

- **Scoped** — a key carries one or both scopes, `Read` and `Execute`, that limit what it can do
- **Hashed at rest** — only a SHA256 hash of the key is stored; the raw key is shown exactly once at creation
- **Bound to a user** — keys are created by a signed-in user and act for that user, never with more than that user may do; when the user is disabled or archived, the key stops working and is revoked
- **Revocable** — by its owner or by an administrator; revoking takes effect immediately on the next request
- **Expiring** — every new key has an expiration date: 90 days by default (or `Beacon:ApiKeys:MaxLifetimeDays`, if lower), at most `MaxLifetimeDays` (365 by default)
- **Project-restricted** — a new key names at least one project and is confined to those projects (keys created before lists were required reach their owner's projects)

A key looks like this:

```
sk-sem_<about 43 base64 characters>
```

The random part is 32 bytes from a cryptographically secure generator, base64-encoded with `+`, `/` and `=` removed, so its length varies slightly (at most 43 characters). Beacon stores its SHA-256 hash and a 16-character display prefix.

## Creating a Key

1. Go to **API Keys** in the left navigation (`/beacon/api-keys`)
2. Click **Generate key**
3. Fill in the dialog:

| Field | Required | Description |
|-------|----------|-------------|
| **Key name** | Yes | A descriptive label, e.g. `CI Pipeline` |
| **Scopes** | Yes (at least one) | `Read` and/or `Execute` — see [Scopes](#scopes). `Execute` needs the Editor role or above |
| **Projects** | Yes (at least one) | The projects the key may use — see [Project restrictions](#project-restrictions). The picker lists only the projects you can read without the Admin bypass |
| **Expiration date** | No | Leave empty for 90 days (or `MaxLifetimeDays`, if lower). A date further away than the configured maximum (365 days by default) is refused |

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

Every new key names the projects it may use: `allowedProjectIds` on the create request (`POST /beacon/api/api-keys`) must list at least one project (`400` otherwise), and each must be a project the creator can read **without the Admin bypass** — an open project, or a restricted one they are a member of (`404` otherwise). An Admin's key cannot name a restricted project the Admin is not a member of. The dialog's project picker lists only these projects (`GET /beacon/api/projects?forApiKey=true`). A key never exceeds its owner: on every request it reaches the projects in its list **where its owner still has access**, at most at the owner's role there, capped by the lower of the key's scope and the owner's global role (a `Read` key reads and runs saved SQL only; a key whose owner may neither read nor write reaches nothing). A key never gets the Admin bypass, even when its owner is an Admin, and never manages project members. See [Project access](/features/authorization/#project-access).

The list is enforced on every REST route and on the [MCP Server](/features/mcp-server/), by the same project access rules on both, fail-closed: a malformed restriction claim denies every project rather than falling open.

**Keys created without a list** (before lists were required) act with their owner's project access, over REST and on MCP: they reach every project their owner may use. Re-issue them with a project list to confine them.

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
- Routes that require the Admin role or a [functional permission](/features/authorization/#functional-permissions) (user management, settings, data-source administration, the key administration below) are never reachable with a key: keys carry no role and no functional permission, whatever their owner holds. A key refused on a route that requires a permission gets `403` with the detail *"API keys cannot use administrative routes; sign in as a user with the X permission."* Automate administrative work as a signed-in user.
- Scopes are attached to the request as claims by the API-key authentication middleware. Interactive browser sessions (cookie/OIDC) carry no scope claims and are not scope-gated — they are governed by user roles instead. Scopes constrain **API keys and Entra callers on MCP only**.
- Requests authenticated with an API key or a bearer token in the `Authorization` header need no antiforgery token, and `GET /beacon/api/csrf` does not issue one to them (it answers `400`). Browser sessions keep sending the token as before.

:::note
Grant the narrowest scope that works. A monitoring integration that only reads configuration and reports needs `Read`; reserve `Execute` for clients that actually run queries or change things.
:::

## Managing Keys

The **API Keys** page (`/beacon/api-keys`) lists your keys with:

| Column | Description |
|--------|-------------|
| **Name** | The label you gave the key |
| **Prefix** | The first 16 characters (e.g. `sk-sem_AbCd12345…`) — enough to identify a key without exposing it |
| **Scopes** | The scopes the key grants (`Read`, `Execute`) |
| **Projects** | The projects the key is restricted to, or `—` for a key created before project lists were required |
| **Created** | Creation timestamp |
| **Last used** | Timestamp of the most recent authenticated request, or `Never` |
| **Expires** | The expiration date, `Never` (keys created before expiry became mandatory), or an `Expired` badge |
| **Status** | `Active` while the key works; otherwise `Inactive` — revoked, expired, or its owner disabled or archived (the administrators' table says `Revoked` for a revoked key) |

### Revoking a key

Click **Revoke** next to an active key and confirm. Revocation is immediate — any integration using that key stops working on its next request. Revoked keys remain in the list (marked `Revoked`) for auditability; they are never silently deleted.

### Administering every user's keys

Admins, Auditors and UserAdmins see a second table on the same page, **All API keys**: every user's keys with their owner. Click an owner to show only that user's keys; Admins and UserAdmins **Revoke** an active key (a UserAdmin who is not an Admin is not offered the keys of an Admin, a super admin or a functional role holder, other than their own). The same is available over the API; each listed key's `canRevoke` says whether the caller may revoke it:

| Method | Route | What it does | Who |
|--------|-------|--------------|-----|
| `GET` | `/beacon/api/api-keys/admin?userId={id}` | Every key, newest first, or only the keys of user `id`; paged and sortable like other lists | Admin, Auditor, UserAdmin |
| `DELETE` | `/beacon/api/api-keys/admin/{id}` | Revokes key `id`, whoever owns it (a UserAdmin who is not an Admin: not the key of an Admin, a super admin or a functional role holder, `403`) | Admin, UserAdmin |

Besides the route's policy, the caller must hold the role in the user store at the time of the request (enabled; super admin, the Admin role, or the [functional permission](/features/authorization/#functional-permissions)) — a session whose user has since been demoted or disabled is refused with `403`. API keys never reach them.

### When the owner leaves

A key works only while its owner can sign in:

- **Disabling a user** (the toggle on the Users page, or saving the user as disabled) revokes all of that user's keys in the same save — and again on every later save of the user as disabled.
- **Enabling the user again does not bring any key back:** the re-enable itself revokes every key of theirs that is still active (for example a key created while they were being disabled, or a key of a user disabled before keys followed their owner). Issue new keys.
- **Archiving (deleting) a user** revokes their keys the same way.
- A key whose owner is disabled, archived or missing is rejected on every request, as is a key with no owner. Disabling, archiving and re-enabling also advance the owner's API-key generation, so a key created while one of those changes was being saved is rejected as well.
- **A role removed from the owner** takes effect on their keys at the next request: a key is capped by its owner's current global role, and keeps `Execute` only while the owner may still write (see [Scopes](#scopes)).
- **An owner who no longer signs in** (opt-in): with `Beacon:ApiKeys:MaxOwnerInactivityDays` set, a key stops working once its owner has not signed in to Beacon with their own identity — a password, a single sign-on, or an identity-provider token on MCP or the REST API, never an API key — for that many days (counted from the account's creation for an owner who never signed in). This covers an owner removed at the identity provider but never disabled in Beacon. The caller gets `401` with *"This API key is inactive: its owner has not signed in to Beacon for N days. The key works again after the owner signs in."*, the refusal is logged with the key id (`owner_inactive`), and the key is listed as inactive. Nothing is revoked: the key works again after the owner signs in. Off by default (`0`). Every sign-in with the owner's own identity counts, including an MCP client or agent that calls `/beacon/mcp` or the REST API with the owner's delegated Entra token, so an agent acting for the owner keeps their keys active as well. Offboard at the identity provider (no new tokens) and disable the Beacon user to end the keys, sessions and delegated access together. An account used only through its API keys (a service account) never signs in: with the rule on, its keys stop working that many days after its last sign-in, or its creation, unless it signs in now and then.

### Expiration

Every new key expires: after 90 days when no date is given (or after `MaxLifetimeDays`, if that is lower), and never later than `Beacon:ApiKeys:MaxLifetimeDays` days after creation (365 by default; see [Configuration → API Keys](/getting-started/configuration/#api-keys)). A date that is not in the future, or beyond the maximum, is refused with `400`.

A key stops working at its expiry instant. An expired key fails validation exactly like a revoked one: the caller receives `401 Unauthorized` with an "Invalid or expired API key" problem response. Expired keys stay visible in the list with an `Expired` badge.

A key created before expiry became mandatory may have no expiration date. Such a key works until it is revoked, and logs a warning with its key id the first time it is used after a restart. Rotate these keys. To retire them all at once, set `Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys` to `true`: such a key then expires `MaxLifetimeDays` after it was created (no stored data changes; unset the option and they work again).

## Using a Key

API keys are passed in the standard `Authorization` header as a Bearer token:

```
Authorization: Bearer sk-sem_YOUR_API_KEY
```

This is the **only** accepted format — there is no `X-Api-Key` header. The authentication middleware only engages when the header starts with `Bearer sk-sem_` (compared case-insensitively); anything else falls through to the other authentication schemes (cookies, JWT).

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
3. **Status checks** — a matching key is rejected if it has been revoked, if its expiry instant has been reached, or if its owner is missing, archived or disabled, or has moved to a later API-key generation than the key's; so is a key whose stored scopes leave no valid scope (none, or only unknown values). All of these return the same `401` problem response: "Invalid or expired API key." The reason is logged at Warning with the key id (`revoked`, `expired`, `owner_disabled`, `owner_archived`, `owner_missing`, `owner_generation_changed`, `no valid scopes`) — never the key or its hash. With `MaxOwnerInactivityDays` set, a key whose owner has not signed in for that long is refused too (`owner_inactive`), with its own `401` message saying so.
4. **Bookkeeping** — the key's *Last used* timestamp is updated.
5. **Identity** — a claims identity is built from the key: its scopes (only `Read` and `Execute`; a key stored with the retired `Admin` scope gets `Execute`, and `Execute` reads as `Read` while the owner has no write permission by their roles or the authorization provider), its project restrictions, and the owner. The scope filter, authorization policies and — when enabled — the owner's role then decide per request.

## Security Best Practices

- **Use least scope.** Create separate keys per integration, each with only the scopes it needs. A read-only reporting job should never hold an `Execute` key.
- **Name only the projects you need.** Every key names its projects; if an integration only works with one project, list only that one. A leaked key exposes the projects it names, not all of them.
- **Keep lifetimes short.** Prefer the shortest expiry that works; lower `Beacon:ApiKeys:MaxLifetimeDays` if your policy demands it. A forgotten key that expires is harmless.
- **Rotate regularly.** There is no in-place rotation: generate a new key, switch the integration over, then revoke the old one. The *Last used* column tells you when the old key has actually gone quiet.
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

**"No projects are available to you" via MCP**
The key reaches no project: none of the projects in its list is one its owner may still use (for example, a project was restricted and the owner is not a member), or its owner may no longer use any project. Check the key's allowed projects and the owner's access. **"Access denied: you do not have access to project N"** means the same for the one project the call named.

**`404` on a project's resources with a key**
The project is not in the key's list, or the key's owner may no longer use it (for example, it was restricted and the owner is not a member).

## Upgrading from 4.5

API keys are now scoped on every route, expire, follow their owner's account and are confined to their projects.

- **`Read` keys are read-only everywhere.** Every `/beacon/api` request other than `GET`/`HEAD`/`OPTIONS` (and `GET /beacon/api/data-sources/{id}/metadata`) answers `403` to a key without `Execute`, whatever `Authorization.Enabled` and `UserManagement.Enabled` say. Integrations that wrote with a `Read` key need an `Execute` key.
- **The `Admin` scope is retired.** New keys carry only `Read` and `Execute`; existing `Admin` keys act as, and are listed as, `Execute`. The MCP discovery documents advertise `scopes_supported: ["Execute"]`.
- **`Execute` follows the owner's current write permission**, checked on every request (the owner's roles and the authorization provider's answer). Existing `Execute` keys of Viewers act as `Read` keys until the owner regains write permission.
- **Who may create keys.** `Execute` keys need the Editor role or above; Viewers create `Read` keys and revoke their own; disabled users create none. Keys are managed from a signed-in browser session only: every `/beacon/api/api-keys` route answers `403` to a key, an MCP caller or a REST bearer token.
- **Every new key expires** (90 days by default, at most `Beacon:ApiKeys:MaxLifetimeDays`). A key without an expiration date from an earlier version warns once per process; rotate it, or set `EnforceMaxLifetimeOnExistingKeys` to expire such keys.
- **Keys follow their owner.** Disabling, archiving or re-enabling a user revokes their active keys in the same save and advances their API-key generation, so a key created during the change is refused too. The `AddApiKeyOwnerGeneration` migration (both providers) adds the generation columns; existing keys start at generation 0 and keep working.
- **Keys are confined to their projects on REST**, not only on MCP, and new keys must name at least one project (`400`; `404` for a project the creator cannot read without the Admin bypass). Keys without a list act with their owner's access — and now reach their owner's projects on MCP too, where before they reached none. Re-issue them with a project list.
- **No antiforgery token for header-authenticated callers**: requests with an API key or bearer token skip it, and `GET /beacon/api/csrf` answers them `400`.
- **Map MCP with `app.MapBeaconMcp()`**, which applies the Execute-scope policy (it needs `builder.Services.AddBeaconApiAuthorization()`). The MCP layer also refuses callers without `Execute` on a weaker mapping.
- **Administrator routes.** `GET /beacon/api/api-keys/admin` and `DELETE /beacon/api/api-keys/admin/{id}` are new, checked against the caller's stored roles on every request.
- **Owner inactivity** (opt-in): `Beacon:ApiKeys:MaxOwnerInactivityDays`.
- **Public API.** `IApiKeyService.GenerateApiKeyAsync` requires the user id and scopes and stores the owner generation; `ValidateApiKeyAsync` must refuse a key of another generation and return the credential with its user and roles loaded, and may throw `ApiKeyOwnerInactiveException`. `ApiKeyEntry` gains `AllowedProjectIds` (positional: recompile). The Execute-scope policy name is `BeaconScopes.ExecuteScopePolicyName`.
