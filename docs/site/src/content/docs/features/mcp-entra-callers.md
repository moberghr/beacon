---
title: Entra ID callers on MCP
description: Let an agent platform call Beacon's MCP endpoint with Microsoft Entra ID tokens, either on behalf of a user or as a system identity, with projects and scope decided by Beacon configuration.
---

Besides [API keys](/features/api-keys/), the [MCP server](/features/mcp-server/) at `/beacon/mcp` accepts **Microsoft Entra ID bearer tokens**. This is how an agent platform calls a Beacon that is embedded in a host application. It supports two modes, and both are first-class:

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

The mapped principal carries `auth_method=mcp_caller`, so the Execute-scope rules for scoped callers apply to it as they do to API keys. A browser session (cookie or SSO) is not a scoped caller and reaches no project on `/beacon/mcp`; browsers use the [MCP Playground](/features/mcp-server/#mcp-playground).

### What a caller reaches

The configured projects are what a caller is mapped to. What it reaches in them is decided on every call by Beacon's [project access](/features/authorization/#project-access), as for API keys and signed-in users:

| Caller | Reaches |
|--------|---------|
| System | its configured projects, restricted or not |
| User with a Beacon user (`AutoProvision`) | its configured projects within its Beacon user's access: open projects, and a restricted project only while the user is a member of it, at the lower of its membership role and its Beacon roles |
| User with a disabled or archived Beacon user | nothing |
| User without a Beacon user (user management off, or `AutoProvision` off) | its configured **open** projects only |

A membership granted or revoked, a project restricted, or a user disabled takes effect on the caller's next call. Provisioned users get no membership automatically: add them as members of the restricted projects they are configured for (`DefaultRoleName` gives them their global role, `Viewer` by default).

What a user caller with a Beacon user may **do** in a project follows that user's role there, capped by the caller's scope: the catalog tools (`get_context`, `search`, `get_documentation`, `get_query_context`) need Read; `ask`, `query`, `dry_run` and `feedback` work ad hoc and need an Editor role there — the user's global role Editor or above and, in a restricted project, an Editor membership (or project admin). A user provisioned with the default `Viewer` role therefore reads the catalog and runs saved-query tools, but its SQL tools answer "Access denied: you may read project N but not run ad-hoc queries in it." (audited). Raise `DefaultRoleName`, or the user's role, to let it run SQL. System callers, and user callers without a Beacon user, are capped by their scope only (`Execute`: SQL tools allowed).

Provisioning therefore changes what the same person can do. A user caller **without** a Beacon user acts within its configured open projects with the SQL tools (its `Execute` scope allows them); once provisioned, it gets its stored role instead — with the default `Viewer`, the catalog only. Both are in the administrator's hands: the caller configuration (its projects and scope) decides the first, `DefaultRoleName` and the user's roles the second. When user callers are configured and `DefaultRoleName` is `Viewer`, Beacon logs this once at startup (Information).

Other routes (`/beacon/api/*`) accept a bearer token only when it names an **existing, enabled, external Beacon
user** by its subject (`sub`) and issuer, or a pre-registered user's subject when only one issuer is configured. A user
that MCP user provisioning created (keyed on `oid` within the tenant) gets **no** REST session from a token. The
session is that user, with the user's **Beacon roles**; no token claim (roles included) reaches it. Any other token gets
`401` with `WWW-Authenticate: Bearer error="invalid_token"`. A configured system identity therefore reaches only
`/beacon/mcp`, where its projects and scope apply.

On `/beacon/mcp` the principal carries only the caller's name, e-mail and display name from the token, plus what the
mapper decides: token roles, groups, `beacon:*` claims and the token's own subject never reach it. Until first-run
setup has created the super admin, `AutoProvision` creates no user and such a caller is rejected.

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

Bearer JWT validation has to be on. In the sample host, turn it on by setting `Beacon:Authentication:Oidc:Enabled` and `Beacon:Authentication:Oidc:McpJwksEndpoint`; the sample then validates the issuer against the OIDC `Authority` and the audience against the OIDC `ClientId`, which may not match the v1/v2 issuer and audience pairs Entra puts in its tokens. A host that configures `Callers` usually sets `ValidIssuers` and `ValidAudiences` itself, as below. In your own host, call `AddBeaconJwtAuthentication(x => { x.EnableBearerAuthentication = true; ... })` and `app.UseBeaconJwtBearerAuthentication()`. The token audience must be Beacon's app registration.

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
          "DefaultRoleName": "Viewer"                    // global role for a newly provisioned user
        },
        "Systems": [
          {
            "Name": "agent-platform",
            "ClientId": "<agent-platform-app-client-id>", // app-only token (azp / appid)
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
| `Users:ProjectIds` | empty | Projects every accepted user is configured for; a restricted one is reached only by its members (see [What a caller reaches](#what-a-caller-reaches)) |
| `Users:GroupProjects` | empty | Additional configured projects per group object id or app role, added together |
| `Users:Scope` | `Execute` | `Read` callers cannot reach `/beacon/mcp` |
| `Users:AutoProvision` | `true` | Create or find a Beacon user (`ExternalId` = `oid`, provider `entra:{tid}`) so audit rows carry a user and project memberships apply. A disabled or archived Beacon user is rejected; within the one minute a caller's user is cached, its calls reach no project instead. This needs user management to be enabled; without it the caller is accepted with no Beacon user |
| `Systems[]:ClientId` / `ObjectId` | none | **Set exactly one per entry.** The host refuses to start otherwise |
| `Systems[]:HostClaims` | empty | The permission set this identity gets inside the host application. It is carried on the caller for in-process API dispatch |

Validation runs at startup. A system entry with both `ClientId` and `ObjectId`, or with neither, a duplicate name, or a non-positive project id, stops the host with a message that names the entry. So do, once any caller is configured: an empty `AllowedTenants`, bearer validation without issuer or audience, and user mode with no role or group requirement and no `AllowAnyTenantUser`. With user mode and `AutoProvision` on, `DefaultRoleName` must be set and must not be the Admin role or the role of a functional permission (every provisioned user would administer Beacon).

With `AutoProvision`, two first requests from the same new user can race to create the Beacon user; the loser
re-reads the row the winner created instead of failing.

:::caution[Group overage]
Entra leaves out the `groups` claim when a user belongs to more than about 200 groups (the "overage" case). For large tenants, prefer **app roles** in `RequiredRoles` and `GroupProjects`, or assign the groups to the app registration so the token only lists relevant groups.
:::

## Connect Claude (claude.ai / Claude Desktop custom connector)

Claude's custom connectors connect by URL and sign the user in with OAuth. Beacon does not run an OAuth server here:
Claude gets a delegated **Entra** token for the user and calls `/beacon/mcp` with it, and Beacon validates the token
and maps the user like any other [user-mode caller](#how-a-call-is-resolved). Claude learns where to sign in from
Beacon's [discovery documents](/features/mcp-server/#discovery-endpoints):

1. `POST /beacon/mcp` without a token answers `401` with `WWW-Authenticate: Bearer resource_metadata="https://<host>/.well-known/oauth-protected-resource"`.
2. That document names the authorization server (`authorization_servers[0]`, the Entra v2 issuer), the scopes to request
   (`scopes_supported`) and the resource (`resource`, the canonical `https://<host>/beacon/mcp` URL).
3. Claude reads Entra's metadata at `https://login.microsoftonline.com/<tenant-id>/v2.0/.well-known/openid-configuration`,
   signs the user in, and requests a token for those scopes, sending the resource URL as the RFC 8707 `resource`
   parameter.

By default the document lists Beacon's internal scope name (`Execute`) and no authorization server unless
Beacon's own OIDC login is on — neither is something Entra understands. Configure it under
`Beacon:Mcp:ProtectedResource`.

### 1. The API app registration

This is the app registration whose tokens Beacon validates (`Validation.ValidAudiences`) — in an embedded setup, usually
the host's API registration.

- **Manifest:** set `api.requestedAccessTokenVersion` to `2` (`accessTokenAcceptedVersion: 2` in the older AAD Graph
  manifest format). Do this first: Entra only lets a v2 app register an HTTPS Application ID URI.
- **Expose an API → Application ID URI:** keep `api://<api-client-id>` and **add the exact MCP URL**,
  `https://<host>/beacon/mcp`, to `identifierUris`. MCP clients send that URL as the `resource` parameter, and Entra
  does *not* ignore it: it must match one of the app's Application ID URIs character for character (no trailing
  slash), or the token request fails with `AADSTS9010010`. Entra may require the host name to be a verified domain of
  the tenant before it accepts an HTTPS Application ID URI.
- **Expose an API → Add a scope:** `mcp.access` (delegated; admins and users, or admins only). Clients request it fully
  qualified, `api://<api-client-id>/mcp.access`. The scope and the `resource` must belong to the same app registration.
- **App roles** (recommended): e.g. `Beacon.Mcp.User`, assigned to the users or groups who may use the connector, and
  listed in `Users:RequiredRoles`.

The token Claude receives is a v2 access token: `iss` is `https://login.microsoftonline.com/<tenant-id>/v2.0`, `aud`
is the API's **client id GUID** (not the `api://` URI and not the MCP URL), and `scp` is `mcp.access`. Keep both audience
forms in `ValidAudiences` so v1 callers of the same API keep working.

### 2. A client app registration for Claude

Entra has no Dynamic Client Registration, so Claude cannot register itself. Create a separate registration for it:

- **Authentication → Add a platform → Web**, redirect URI `https://claude.ai/api/mcp/auth_callback`.
- **Certificates & secrets:** create a client secret (it is a confidential client). Note its expiry — the connector
  stops working when it expires.
- **API permissions → Add a permission → APIs my organization uses →** the API registration → **Delegated** →
  `mcp.access`, then **Grant admin consent** for the tenant.

### 3. Beacon configuration

```csharp
builder.Services.AddBeaconJwtAuthentication(x =>
{
    x.EnableBearerAuthentication = true;
    x.Validation.JwksEndpoint = "https://login.microsoftonline.com/<tenant-id>/discovery/v2.0/keys";
    x.Validation.ValidIssuers = ["https://login.microsoftonline.com/<tenant-id>/v2.0"];
    x.Validation.ValidAudiences = ["<api-client-id>", "api://<api-client-id>"];
});
```

```jsonc
{
  "Beacon": {
    "PublicBaseUrl": "https://<host>",                  // resource = https://<host>/beacon/mcp
    "Mcp": {
      "ProtectedResource": {
        "AuthorizationServers": ["https://login.microsoftonline.com/<tenant-id>/v2.0"],
        // or, instead of listing it: "DeriveAuthorizationServerFromJwtIssuer": true
        "ScopesSupported": ["api://<api-client-id>/mcp.access"]
      },
      "Callers": {
        "AllowedTenants": ["<tenant-id>"],
        "Users": { "Enabled": true, "RequiredRoles": ["Beacon.Mcp.User"], "ProjectIds": [1] }
      }
    }
  }
}
```

| Setting (`Beacon:Mcp:ProtectedResource`) | Default | Meaning |
|---------|---------|---------|
| `AuthorizationServers` | empty | Issuers published as `authorization_servers`, verbatim and in order (clients use the first). Use the **v2** issuer: it equals the `issuer` in Entra's v2 metadata and the `iss` of the tokens. Wins over everything below |
| `DeriveAuthorizationServerFromJwtIssuer` | `false` | With no explicit list, publish the bearer JWT issuer (`Validation.ValidIssuer(s)`). With several, the one ending in `/v2.0` is chosen; templated issuers (`{tenantid}`) are skipped |
| `ScopesSupported` | empty | Scopes published in `scopes_supported` (and the server card) instead of `Execute` |

With none of these set the document lists Beacon's OIDC authority when SSO is on, and the `Execute` scope. An explicit authorization server without `ScopesSupported`, or derivation with no usable
issuer, is logged as a warning at startup. The `WWW-Authenticate` challenge, the `resource` in the metadata and the
server card's `endpoint` and `resource_metadata` all derive from the same `PublicBaseUrl` (or the forwarded request
host when it is unset), so they always agree.

### 4. Add the connector in Claude

:::caution[Verify against Anthropic's current connector docs]
This repository cannot test Claude's UI, so the steps below are a best reading of it at the time of writing. Check
Anthropic's current custom-connector documentation for menu names, the redirect URI, and plan or admin requirements.
:::

- In claude.ai, **Settings → Connectors → Add custom connector** (an organization owner may have to add it for the
  organization first). Enter the server URL, `https://<host>/beacon/mcp`.
- Open **Advanced settings** and enter the **OAuth Client ID** and **OAuth Client Secret** of the Claude client
  registration from step 2.
- Connect. Claude opens the Entra sign-in, the user consents (or admin consent already covers it), and the tools appear.
  Claude Desktop uses the connectors of the signed-in claude.ai account.

Things to check when the connection fails, none of which Beacon can see:

- **PKCE advertisement.** Entra's metadata does not publish `code_challenge_methods_supported`, although Entra supports
  PKCE (`S256`). The MCP authorization spec tells clients to refuse an authorization server that does not advertise
  it; whether Claude enforces that for Entra is for Anthropic's docs to say.
- **Refresh tokens.** If Claude asks the user to sign in again every hour, add `offline_access` to `ScopesSupported`
  (clients request the listed scopes verbatim).
- **`AADSTS9010010`** — the `resource` URL Claude sent is not an Application ID URI of the API registration, or a
  requested scope belongs to another registration. Compare the `resource` in
  `/.well-known/oauth-protected-resource` with the registration's `identifierUris`.
- **`401` with `error="invalid_token"`** — Beacon rejected the token: check `iss`/`aud` against `ValidIssuers` /
  `ValidAudiences` (a v2 token's `aud` is the client id GUID). A malformed bearer value gets the same `401`, never a `500`.
- **`403`** — the token is valid but the user is not an accepted caller (`Users:Enabled`, `RequiredRoles` /
  `RequiredGroups`, `AllowedTenants`).
- **`503` with `Retry-After`** — Beacon could not fetch the Entra JWKS and has no cached keys.

## Audit and privacy

Every MCP tool call writes an audit row, as before. For a JWT caller, the row also records:

- `CallerKind`: `User` or `System`.
- `CallerHash`: the lowercase hex HMAC-SHA256 of `{tid}:{subject}`, where the subject is the token's `oid`, else its `azp` / `appid`, else its `sub`. It is stable per identity and never the raw value. The HMAC key is derived for this purpose only from `Beacon:Mcp:CallerHashKey` when it is set, else from `Beacon:EncryptionKey`, so someone who holds the tenant's list of `oid`s cannot reverse the hashes. Without `CallerHashKey`, changing the encryption key changes every caller's hash, so rows written before and after the change no longer group together: before changing the encryption key, set `Beacon:Mcp:CallerHashKey` (a secret) to the key in use now, and the hashes stay the same (see [Configuration → Replacing the Key](/getting-started/configuration/#replacing-the-key)).
- `UserId`: the provisioned Beacon user, in user mode.

Logs carry only the caller hash, never the raw `oid`, email or token.

## Replacing the mapper

A host can replace the default mapping. For example, it can map an Entra user to its own admin user and permission set. To do this, register an `IMcpCallerMapper` before `AddBeaconServices`, which registers the default with `TryAdd`, or replace the registration afterwards:

```csharp
builder.Services.AddScoped<IMcpCallerMapper, AcmeMcpCallerMapper>();
```

The mapper receives the raw token claims (`oid`, `tid`, `groups`, and so on) and returns an `McpCaller`, or `null` to reject the caller. `ConfiguredMcpCallerMapper` is public, so a custom mapper can delegate to it and adjust the result. Inject `McpCallerSubjectHasher`, which `AddBeaconServices` registers as a singleton, to produce the same audit hash.

## Upgrading from 4.5: project access

- **User callers need membership on restricted projects.** A user caller reaches a configured project that is restricted only while its Beacon user is a member of it; open configured projects are reached as before. Add the provisioned users as members of the restricted projects they are configured for.
- **User callers without a Beacon user reach only their configured open projects** (before: every configured project).
- **A disabled or archived user's caller reaches nothing from its next call**, also within the minute its mapping is cached.
- **System callers are unchanged**: they reach their configured projects, restricted or not.
- **User callers with a Beacon user need Editor to run SQL tools.** `ask`, `query`, `dry_run` and `feedback` need the user's role in the project to be Editor or above (global role, and membership on a restricted project); the catalog tools need Read only. Users provisioned with the default `DefaultRoleName` `Viewer` now read the catalog but are refused the SQL tools: give them the Editor role (and Editor membership on restricted projects) if they should run SQL.
- **Host endpoint and saved-query tools** follow the same access: host endpoint tools need the caller to reach the host's project, and saved-query tools are listed only where the caller may run saved SQL.
- **New projects are restricted.** A project created after the upgrade by an active Beacon user is restricted, so a user caller configured for it reaches it only once its provisioned Beacon user is a member — with an Editor membership to run SQL tools. Checklist for provisioned users: the default `Viewer` role reads the catalog only; restricted configured projects need membership; the SQL tools need Editor (global role and membership).

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

### Hosts whose cookie authentication redirects

A host whose MVC cookie authentication answers an unauthenticated request with a `302` to its login page breaks MCP clients: an MCP
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
