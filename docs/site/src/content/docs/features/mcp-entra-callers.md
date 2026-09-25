---
title: Entra ID callers on MCP
description: Let an agent platform call Beacon's MCP endpoint with Microsoft Entra ID tokens, either on behalf of a user or as a system identity, with projects and scope decided by Beacon configuration.
---

Besides [API keys](/features/api-keys/), the [MCP server](/features/mcp-server/) at `/beacon/mcp` accepts **Microsoft Entra ID bearer tokens**. This is how an agent platform (for example AIProxy / Kvika Work) calls a Beacon that is embedded in a host application. It supports two modes, and both are first-class:

| Mode | Token | Identified by |
|------|-------|---------------|
| **User** | A delegated token issued on behalf of the user (it carries `scp`) | The user's `oid`, plus `roles` and `groups` |
| **System** | An app-only token from the client-credentials flow (`idtyp=app`, or no `scp` + `roles` + `oid == sub`) | The calling app's `azp` / `appid` |
| **System** (service user) | A token for a dedicated Entra service user | The service user's `oid` |

The token only proves **who** is calling. **What** the caller may do (which projects, and which scope) comes from Beacon configuration. A token cannot grant itself projects or a scope: any `allowed_projects`, `scope`, `auth_method`, `api_key_id` or `caller_*` claim inside the token is discarded.

## How a call is resolved

1. The bearer token is validated (signature against the JWKS endpoint, issuer, audience and lifetime). The JWKS is
   cached: it is fetched once and refreshed hourly, or earlier — at most once every 5 minutes — when a token is
   signed with a key id the cached set does not hold (key rotation). A failed refresh keeps the last good keys.
2. On `/beacon/mcp`, Beacon passes the validated claims to `IMcpCallerMapper`. The default `ConfiguredMcpCallerMapper`
   first rejects the token unless:
   - its `tid` is one of `AllowedTenants` (a token without `tid` is rejected);
   - its `aud` is one of the bearer audiences Beacon validates (checked again here, as defense in depth);
   - it is an **access token** of a known kind (see [Token classification](#token-classification)); ID tokens and
     tokens that cannot be classified are rejected.

   It then checks, in this order:
   1. A configured **system** whose `ObjectId` equals the token's `oid` (a service user or a service principal).
   2. A configured **system** whose `ClientId` equals `azp`/`appid`, but **only for app-only tokens**. A delegated user token (or an ID token) issued to the same app also carries its `azp`, so it never matches a `ClientId` system.
   3. **User mode**, for delegated tokens, when it is enabled and the caller has one of the required roles or groups
      (or `AllowAnyTenantUser` is set).
3. A matched caller gets the Beacon claims that API keys already use: `allowed_projects`, `scope` (`Read` or `Execute`), and the Beacon user id as the name identifier when the user is provisioned. It also gets `caller_kind` and `caller_hash`.
4. The route's Execute-scope policy then applies to JWT callers exactly as it does to API keys. **`Read` callers and unknown callers get `403`**, and `Execute` callers pass.

A caller that is neither a configured system nor an accepted user is **unknown**. It gets no projects and no scope, and it is rejected. This fails closed.

Other routes (`/beacon/api/*`) keep their existing JWT behaviour: token claims pass through, apart from the reserved ones listed above, and they are not scope-gated.

### Token classification

"No `scp`" alone does not make a token app-only: an ID token, or any other token without `scp`, carries the
requesting app's `azp` too and would otherwise be taken for that app's system identity. The mapper therefore needs a
positive signal:

| Token | Classified as |
|-------|---------------|
| Has `nonce` | ID token → **rejected** |
| `idtyp = app` and no `scp` | App-only |
| `idtyp = app` and `scp` | Contradictory → **rejected** |
| Has `scp` | Delegated (user) |
| No `scp`, has `roles`, and `oid == sub` (a service principal's own token) | App-only |
| Anything else (e.g. neither `scp` nor `roles`) | **Rejected** |

**Entra app registration requirement.** For every system that calls with the client-credentials flow, either add the
optional access-token claim **`idtyp`** to Beacon's app registration (Token configuration → Add optional claim →
Access → `idtyp`; recommended), or assign the calling app at least one **app role** on Beacon's app registration so
its tokens carry `roles`. A client-credentials token with neither is rejected.

## Configuration

Bearer JWT validation has to be on. In the sample host, turn it on by setting `Beacon:Authentication:Oidc:Enabled` and `McpJwksEndpoint`. In your own host, call `AddBeaconJwtAuthentication(x => { x.EnableBearerAuthentication = true; ... })` and `app.UseBeaconJwtBearerAuthentication()`. The token audience must be Beacon's app registration.

**Issuer and audience are mandatory once any caller is configured.** Entra's JWKS signs tokens for every tenant and
every resource, so a valid signature proves nothing about who the token is for. When `Users:Enabled` is on or any
`Systems` entry exists, the host refuses to start unless bearer JWT authentication is enabled, `Validation.ValidIssuer`
(or `ValidIssuers`) and `Validation.ValidAudience` (or `ValidAudiences`) are set, and `ValidateIssuer` /
`ValidateAudience` are left on:

```csharp
builder.Services.AddBeaconJwtAuthentication(x =>
{
    x.EnableBearerAuthentication = true;
    x.Validation.JwksEndpoint = "https://login.microsoftonline.com/<tenant-id>/discovery/v2.0/keys";
    x.Validation.ValidIssuers =
    [
        "https://login.microsoftonline.com/<tenant-id>/v2.0",   // v2 access tokens
        "https://sts.windows.net/<tenant-id>/"                  // v1 access tokens
    ];
    x.Validation.ValidAudiences = ["api://<beacon-app-client-id>", "<beacon-app-client-id>"];
});
```

The callers are configured under `Beacon:Mcp:Callers`:

```jsonc
{
  "Beacon": {
    "Mcp": {
      "Callers": {
        "AllowedTenants": ["<entra-tenant-id>"],          // required once any caller is configured
        "Users": {
          "Enabled": true,
          "RequiredRoles": ["Beacon.Mcp.User"],          // any-of, combined with RequiredGroups
          "RequiredGroups": ["<entra-group-object-id>"],  // empty + empty fails startup...
          "AllowAnyTenantUser": false,                   // ...unless this is set to true explicitly
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
| `AllowedTenants` | empty | Entra tenant ids (`tid`) whose tokens may be mapped. **Required and non-empty** once `Users:Enabled` is on or any system is configured |
| `Users:Enabled` | `false` | Accept delegated user tokens at all |
| `Users:RequiredRoles` / `RequiredGroups` | empty | Any-of across both lists. Both empty fails startup unless `AllowAnyTenantUser` is `true` |
| `Users:AllowAnyTenantUser` | `false` | Explicit opt-in to admit **every** user of the allowed tenants when no role or group is required |
| `Users:ProjectIds` | empty | Projects every accepted user can reach |
| `Users:GroupProjects` | empty | Additional projects per group object id or app role, added together |
| `Users:Scope` | `Execute` | `Read` callers cannot reach `/beacon/mcp` |
| `Users:AutoProvision` | `true` | Create or find a Beacon user (`ExternalId` = `oid`, provider `entra:{tid}`) so audit rows carry a user. A disabled or archived Beacon user is rejected. This needs user management to be enabled; without it the caller is accepted with no Beacon user |
| `Systems[]:ClientId` / `ObjectId` | none | **Set exactly one per entry.** The host refuses to start otherwise |
| `Systems[]:HostClaims` | empty | The permission set this identity gets inside the host application. It is carried on the caller for in-process API dispatch |

Validation runs at startup. A system entry with both `ClientId` and `ObjectId`, or with neither, a duplicate name, or a non-positive project id, stops the host with a message that names the entry. So do, once any caller is configured: an empty `AllowedTenants`, bearer validation without issuer or audience, and user mode with no role or group requirement and no `AllowAnyTenantUser`.

With `AutoProvision`, two first requests from the same new user can race to create the Beacon user; the loser
re-reads the row the winner created instead of failing.

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

## Upgrading from 4.3

The gateway release changes how JWT callers reach MCP. Before upgrading a host that accepts bearer tokens:

- **JWT callers on `/beacon/mcp` now need `Beacon:Mcp:Callers`.** A validated token alone no longer reaches MCP:
  without a matching system or accepted user the caller gets `403`. Configure `Systems` / `Users` for every agent
  platform that calls MCP with a token. API keys are unaffected.
- **Reserved claims are stripped from tokens on every route.** `allowed_projects`, `scope`, `auth_method`,
  `api_key_id` and `caller_*` inside a token are discarded on `/beacon/api/*` too, so a token can no longer carry its
  own project restriction or scope.
- **New mandatory settings once any caller is configured:** `Beacon:Mcp:Callers:AllowedTenants`, bearer
  `Validation.ValidIssuer(s)` and `Validation.ValidAudience(s)` with validation on, and either `RequiredRoles` /
  `RequiredGroups` or `Users:AllowAnyTenantUser = true` for user mode. The host fails at startup with a message that
  names what is missing.
- **App-only tokens need a positive signal** — the `idtyp` optional claim or an app role (see
  [Token classification](#token-classification)).
- **New migrations**, in both providers: `AddMcpAuditCallerIdentity` (audit caller kind/hash),
  `AddHostManagedDataSources`, `AddProjectImportedDocuments`, `AddQueryMcpTools` and `AddHostManagedProjects`
  (`Project.HostManagedKey`). Apply them before the new version starts.

### Netgiro: cookie authentication and `/beacon/mcp`

Web.Admin's MVC cookie authentication answers an unauthenticated request with a `302` to its login page. An MCP
client cannot follow that, and an agent then sees an HTML login page instead of an auth error. Make the cookie
events return status codes for the MCP path:

```csharp
options.Events.OnRedirectToLogin = ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/beacon/mcp"))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
};
options.Events.OnRedirectToAccessDenied = ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/beacon/mcp"))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
};
```
