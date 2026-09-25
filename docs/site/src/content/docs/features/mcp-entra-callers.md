---
title: Entra ID callers on MCP
description: Let an agent platform call Beacon's MCP endpoint with Microsoft Entra ID tokens, either on behalf of a user or as a system identity, with projects and scope decided by Beacon configuration.
---

Besides [API keys](/features/api-keys/), the [MCP server](/features/mcp-server/) at `/beacon/mcp` accepts **Microsoft Entra ID bearer tokens**. This is how an agent platform (for example AIProxy / Kvika Work) calls a Beacon that is embedded in a host application. It supports two modes, and both are first-class:

| Mode | Token | Identified by |
|------|-------|---------------|
| **User** | A delegated token issued on behalf of the user (it carries `scp`) | The user's `oid`, plus `roles` and `groups` |
| **System** | An app-only token from the client-credentials flow (`idtyp=app`, no `scp`) | The calling app's `azp` / `appid` |
| **System** (service user) | A token for a dedicated Entra service user | The service user's `oid` |

The token only proves **who** is calling. **What** the caller may do (which projects, and which scope) comes from Beacon configuration. A token cannot grant itself projects or a scope: any `allowed_projects`, `scope`, `auth_method`, `api_key_id` or `caller_*` claim inside the token is discarded.

## How a call is resolved

1. The bearer token is validated (signature against the JWKS endpoint, issuer, audience and lifetime).
2. On `/beacon/mcp`, Beacon passes the validated claims to `IMcpCallerMapper`. The default `ConfiguredMcpCallerMapper` checks, in this order:
   1. A configured **system** whose `ObjectId` equals the token's `oid` (a service user or a service principal).
   2. A configured **system** whose `ClientId` equals `azp`/`appid`, but **only for app-only tokens**. A delegated user token issued to the same app also carries its `azp`, so it never matches a `ClientId` system.
   3. **User mode**, for delegated tokens, when it is enabled and the caller has one of the required roles or groups.
3. A matched caller gets the Beacon claims that API keys already use: `allowed_projects`, `scope` (`Read` or `Execute`), and the Beacon user id as the name identifier when the user is provisioned. It also gets `caller_kind` and `caller_hash`.
4. The route's Execute-scope policy then applies to JWT callers exactly as it does to API keys. **`Read` callers and unknown callers get `403`**, and `Execute` callers pass.

A caller that is neither a configured system nor an accepted user is **unknown**. It gets no projects and no scope, and it is rejected. This fails closed.

Other routes (`/beacon/api/*`) keep their existing JWT behaviour: token claims pass through, apart from the reserved ones listed above, and they are not scope-gated.

## Configuration

Bearer JWT validation has to be on. In the sample host, turn it on by setting `Beacon:Authentication:Oidc:Enabled` and `McpJwksEndpoint`. In your own host, call `AddBeaconJwtAuthentication(x => { x.EnableBearerAuthentication = true; ... })` and `app.UseBeaconJwtBearerAuthentication()`. The token audience must be Beacon's app registration.

The callers are configured under `Beacon:Mcp:Callers`:

```jsonc
{
  "Beacon": {
    "Mcp": {
      "Callers": {
        "Users": {
          "Enabled": true,
          "RequiredRoles": ["Beacon.Mcp.User"],          // any-of, combined with RequiredGroups
          "RequiredGroups": ["<entra-group-object-id>"],  // empty + empty = every user in the tenant
          "ProjectIds": [1],                             // granted to every accepted user
          "GroupProjects": {                             // group id or app role -> more projects (additive)
            "<entra-group-object-id>": [2, 3],
            "Beacon.Finance": [4]
          },
          "Scope": "Execute",                            // Read | Execute
          "AutoProvision": true,                         // find or create a Beacon user keyed on oid
          "DefaultRoleName": "Viewer"                    // role for a newly provisioned user
        },
        "Systems": [
          {
            "Name": "aiproxy",
            "ClientId": "<aiproxy-app-client-id>",       // app-only token (azp / appid)
            "ProjectIds": [1, 2],
            "Scope": "Execute",
            "HostClaims": [ { "Type": "permission", "Value": "ViewReports" } ]
          },
          {
            "Name": "routine-runner",
            "ObjectId": "<service-user-object-id>",      // dedicated Entra service user (oid)
            "ProjectIds": [2],
            "Scope": "Execute"
          }
        ]
      }
    }
  }
}
```

| Setting | Default | Meaning |
|---------|---------|---------|
| `Users:Enabled` | `false` | Accept delegated user tokens at all |
| `Users:RequiredRoles` / `RequiredGroups` | empty | Any-of across both lists. When both are empty, no extra requirement applies |
| `Users:ProjectIds` | empty | Projects every accepted user can reach |
| `Users:GroupProjects` | empty | Additional projects per group object id or app role, added together |
| `Users:Scope` | `Execute` | `Read` callers cannot reach `/beacon/mcp` |
| `Users:AutoProvision` | `true` | Create or find a Beacon user (`ExternalId` = `oid`, provider `entra:{tid}`) so audit rows carry a user. A disabled or archived Beacon user is rejected. This needs user management to be enabled; without it the caller is accepted with no Beacon user |
| `Systems[]:ClientId` / `ObjectId` | none | **Set exactly one per entry.** The host refuses to start otherwise |
| `Systems[]:HostClaims` | empty | The permission set this identity gets inside the host application. It is carried on the caller for in-process API dispatch |

Validation runs at startup. A system entry with both `ClientId` and `ObjectId`, or with neither, a duplicate name, or a non-positive project id, stops the host with a message that names the entry.

:::caution[Group overage]
Entra leaves out the `groups` claim when a user belongs to more than about 200 groups (the "overage" case). For large tenants, prefer **app roles** in `RequiredRoles` and `GroupProjects`, or assign the groups to the app registration so the token only lists relevant groups.
:::

## Audit and privacy

Every MCP tool call writes an audit row, as before. For a JWT caller, the row also records:

- `CallerKind`: `User` or `System`.
- `CallerHash`: the lowercase hex HMAC-SHA256 of `{tid}:{oid}` (or of `{tid}:{appid}` when there is no `oid`). It is stable per identity and never the raw value. The HMAC key is derived from `Beacon:EncryptionKey` for this purpose only, so someone who holds the tenant's list of `oid`s cannot reverse the hashes. Rotating the encryption key changes every caller's hash, so rows written before and after a rotation no longer group together.
- `UserId`: the provisioned Beacon user, in user mode.

Logs carry only the caller hash, never the raw `oid`, email or token.

## Replacing the mapper

A host can replace the default mapping. For example, it can map an Entra user to its own admin user and permission set. To do this, register an `IMcpCallerMapper` before `AddBeaconServices`, which registers the default with `TryAdd`, or replace the registration afterwards:

```csharp
builder.Services.AddScoped<IMcpCallerMapper, NetgiroMcpCallerMapper>();
```

The mapper receives the raw token claims (`oid`, `tid`, `groups`, and so on) and returns an `McpCaller`, or `null` to reject the caller. `ConfiguredMcpCallerMapper` is public, so a custom mapper can delegate to it and adjust the result. Inject `McpCallerSubjectHasher`, which `AddBeaconServices` registers as a singleton, to produce the same audit hash.
