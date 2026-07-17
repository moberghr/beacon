---
title: API Keys
description: Scoped, SHA256-hashed API keys for programmatic access, with Read/Execute/Admin scopes, optional project restriction, and expiry.
---

API keys give programmatic clients — CI pipelines, monitoring bots, and MCP clients — scoped access to Beacon without an interactive login. Keys are SHA256-hashed at rest, carry explicit scopes, and can be restricted to specific projects and given an expiration date.

## Overview

Every API key is:

- **Scoped** — a key carries one or more scopes (`Read`, `Execute`, `Admin`) that limit what it can do
- **Hashed at rest** — only a SHA256 hash of the key is stored; the raw key is shown exactly once at creation
- **Bound to a user** — keys are created by an authenticated user, so audit trails correlate key activity back to that user
- **Revocable** — revoking a key takes effect immediately on the next request
- **Optionally expiring** — set an expiration date, or leave it empty for a non-expiring key
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
| **Scopes** | Yes (at least one) | `Read`, `Execute`, and/or `Admin` — see [Scopes](#scopes) |
| **Expiration date** | No | Leave empty for a key that does not expire |

4. Click **Generate key** — the raw key is displayed once

:::caution
**The raw key is shown exactly once.** Beacon stores only a SHA256 hash — the plaintext key cannot be recovered later. Copy it immediately and store it in a secrets manager. If you lose it, revoke the key and generate a new one.
:::

The dialog blocks accidental dismissal (backdrop click and Escape are disabled) until you confirm you have copied the key.

### Project restrictions

Keys can optionally be restricted to specific projects via the `allowedProjectIds` field on the create request (`POST /beacon/api/api-keys`). A restricted key can only resolve and query the projects in its list — this is enforced fail-closed on the [MCP Server](/features/mcp-server/): if the restriction claim is missing or malformed, all project access is denied rather than falling open.

## Scopes

| Scope | Grants |
|-------|--------|
| `Read` | Query data, read configs and reports |
| `Execute` | Trigger scans and run jobs |
| `Admin` | Full access, including user management |

**How enforcement works:**

- SQL-executing REST endpoints (e.g. query preview and step preview under `/beacon/api/queries`) require the `Execute` or `Admin` scope for API-key callers.
- Scopes are attached to the request as claims by the API-key authentication middleware and evaluated by authorization policies on each request.
- Interactive browser sessions (cookie/OIDC) carry no scope claims and are not scope-gated — they are governed by user roles instead. Scopes constrain **API-key identities only**.

:::note
Grant the narrowest scope that works. A monitoring integration that only reads configuration and reports needs `Read`; reserve `Execute` for clients that actually run queries, and `Admin` for automation that genuinely manages the instance.
:::

## Managing Keys

The **API Keys** page (`/api-keys`) lists every key with:

| Column | Description |
|--------|-------------|
| **Name** | The label you gave the key |
| **Prefix** | The first 16 characters (e.g. `sk-sem_AbCd12345…`) — enough to identify a key without exposing it |
| **Scopes** | The scopes granted to the key |
| **Created** | Creation timestamp |
| **Last used** | Timestamp of the most recent authenticated request, or `Never` |
| **Expires** | The expiration date, `Never`, or an `Expired` badge |
| **Status** | `Active` or `Revoked` |

### Revoking a key

Click **Revoke** next to an active key and confirm. Revocation is immediate — any integration using that key stops working on its next request. Revoked keys remain in the list (marked `Revoked`) for auditability; they are never silently deleted.

### Expiration

An expired key fails validation exactly like a revoked one: the caller receives `401 Unauthorized` with an "Invalid or expired API key" problem response. Expired keys stay visible in the list with an `Expired` badge.

## Using a Key

API keys are passed in the standard `Authorization` header as a Bearer token:

```
Authorization: Bearer sk-sem_YOUR_API_KEY
```

This is the **only** accepted format — there is no `X-Api-Key` header. The authentication middleware only engages when the header starts with `Bearer sk-sem_`; anything else falls through to the other authentication schemes (cookies, JWT).

### With the REST API

Read endpoints under `/beacon/api/*` accept the key directly:

```bash
# List projects
curl "https://your-beacon-host/beacon/api/projects" \
  -H "Authorization: Bearer sk-sem_YOUR_API_KEY"

# List API keys (metadata only — never the raw keys)
curl "https://your-beacon-host/beacon/api/api-keys" \
  -H "Authorization: Bearer sk-sem_YOUR_API_KEY"
```

The full endpoint surface is described by the OpenAPI document at `/openapi/v1.json`.

### With the MCP Server

API keys are the authentication mechanism for the [MCP Server](/features/mcp-server/) at `/beacon/mcp`. Add the key to your MCP client configuration:

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
3. **Status checks** — a matching key is rejected if it has been revoked or if its expiration date has passed. Both cases return the same `401` problem response: "Invalid or expired API key."
4. **Bookkeeping** — the key's *Last used* timestamp is updated.
5. **Identity** — a claims identity is built from the key: its scopes, its project restrictions, and the linked user. Authorization policies then evaluate those claims per endpoint.

## Security Best Practices

- **Use least scope.** Create separate keys per integration, each with only the scopes it needs. A read-only reporting job should never hold an `Execute` or `Admin` key.
- **Restrict to projects.** If an integration only works with one project, restrict the key to that project. A leaked restricted key exposes one project, not all of them.
- **Set expiration dates.** Prefer expiring keys for anything short-lived (contractors, proofs of concept, temporary integrations). A forgotten key that expires is harmless; a forgotten key that lives forever is a liability.
- **Rotate regularly.** Generate a new key, switch the integration over, then revoke the old one. The *Last used* column tells you when the old key has actually gone quiet.
- **Never commit keys.** Keys must not appear in source code, config files under version control, CI logs, or chat. Inject them via environment variables or a secrets manager. If a key is ever committed — even to a private repository — revoke it immediately.
- **Revoke on suspicion.** Revocation is instant and free. If there is any doubt a key has been exposed, revoke first and re-issue after.
- **Audit periodically.** Review the API Keys page for keys with `Never` or stale *Last used* values and revoke what is no longer needed.

:::note
Beacon never stores or logs the plaintext key. If you find yourself needing to "look up" an existing key's value, that is by design impossible — generate a new key instead.
:::

## Troubleshooting

**`401 Unauthorized` — "Invalid or expired API key"**
The key was recognized as an API key but failed validation. Check that it hasn't been revoked, hasn't passed its expiration date, and was copied in full (keys are long — a truncated paste is the most common cause).

**Request behaves as unauthenticated**
The middleware only engages on headers that begin with `Bearer sk-sem_`. Verify the header is exactly `Authorization: Bearer sk-sem_...` — a missing `Bearer ` prefix or a custom header name (e.g. `X-Api-Key`) is silently ignored.

**`403` on query execution endpoints**
SQL-executing endpoints require the `Execute` or `Admin` scope. A `Read`-only key can browse but not run queries — generate a key with the `Execute` scope.

**"No project found" via MCP**
The key's project restriction resolved to no accessible projects. Check the key's allowed projects, or create an unrestricted key.
