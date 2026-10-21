---
title: MCP & AI clients
description: Expose Beacon's MCP server from your host, decide how AI clients authenticate (API keys or Entra ID callers), publish discovery metadata, and connect Claude Code, Claude, Cursor and VS Code.
---

Beacon's MCP server lets AI assistants explore and query your data through eight tools, always project-scoped, read-only, masked and audited. This page wires it into your host and connects clients. The tools themselves are described on [MCP server](/features/mcp-server/).

## Wiring

```csharp
builder.Services.AddBeaconApiAuthorization();   // registers the Execute-scope policy MCP needs
builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconMcp();
// …
app.UseMcpResourceMetadataChallenge();           // before ApiKeyAuthMiddleware
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseBeaconJwtBearerAuthentication();          // only for Entra / JWT callers
// … authentication, cookie, authorization …
app.MapBeaconMcp();                              // /beacon/mcp, Execute scope required
app.MapMcpDiscovery();                           // anonymous discovery documents
```

| Item | Value |
|---|---|
| Endpoint | `/beacon/mcp` |
| Transport | Streamable HTTP |
| Authentication | `Authorization: Bearer sk-sem_…` (API key) or `Authorization: Bearer <Entra access token>` |
| Required scope | **Execute** (a Read-scoped key gets 403) |
| Sessions | Clients that start with `initialize` (most clients today) get an `Mcp-Session-Id` (idle timeout 30 minutes) and need **session affinity** when you run several instances. Clients speaking the stateless protocol revision `2026-07-28` need no session. A request that is neither (no session and no `2026-07-28` metadata) is refused with 400. |

`MapBeaconMcp()` maps the route with the Execute-scope policy, and two filters inside the MCP layer refuse any caller without Execute even if the route were mapped with a weaker policy. Do not map it yourself with `MapMcp(...)` and a bare `RequireAuthorization()`.

:::note[Browser sessions]
A signed-in browser session, Beacon's or your host's, is refused on `/beacon/mcp` with `403`: the route takes an API key or a mapped bearer token only. People try the tools in the UI instead: **MCP playground** (`/beacon/mcp-playground`).
:::

## Public URL and discovery

Clients that connect by URL (claude.ai, Claude Desktop connectors, VS Code) discover how to authenticate from three anonymous documents:

| Path | Content |
|---|---|
| `/.well-known/oauth-protected-resource` | RFC 9728 protected-resource metadata: `resource` (`{base}/beacon/mcp`), `scopes_supported`, `authorization_servers`, bearer in header only |
| `/.well-known/oauth-protected-resource/beacon/mcp` | The same, at the path-inserted location |
| `/.well-known/mcp/server-card.json` | Server card: name, transport, endpoint, authentication, the eight built-in tools |

Every 401 from `/beacon/mcp` carries `WWW-Authenticate: Bearer resource_metadata="{base}/.well-known/oauth-protected-resource"`.

**Set `Beacon:PublicBaseUrl`** (for example `https://beacon.example.com`) in every proxied or public deployment. Without it, the documents and challenges are built from the request's `Host` header, which the client controls; Beacon warns at startup.

To point OAuth-capable clients at your identity provider:

```json
{
  "Beacon": {
    "PublicBaseUrl": "https://beacon.example.com",
    "Mcp": {
      "ProtectedResource": {
        "AuthorizationServers": [ "https://login.microsoftonline.com/<tenant-id>/v2.0" ],
        "ScopesSupported": [ "api://<beacon-app-id>/mcp.access" ]
      }
    }
  }
}
```

| Key | Default | What it does |
|---|---|---|
| `AuthorizationServers` | None | Issuers published as `authorization_servers` (each must equal the server metadata's `issuer`). Takes precedence. |
| `DeriveAuthorizationServerFromJwtIssuer` | `false` | Publish the bearer-JWT issuer instead (prefers an Entra `/v2.0` issuer). |
| `ScopesSupported` | `["Execute"]` | Scopes published to clients. Set your identity provider's scope when you publish an external authorization server. |

With none of these set and OIDC enabled, Beacon publishes its OIDC authority.

## Option A: API keys

The simplest path: each person creates a key for themselves.

1. In **API keys** (`/beacon/api-keys`), create a key with the **Execute** scope, limited to the projects the assistant should use, with a short expiry. (Scripts can do the same through `POST /beacon/api/api-keys`; see [headless first run](/integrate/authentication/#headless-first-run-scripts-and-ci).)
2. Copy it. It is shown once.
3. Configure the client with `Authorization: Bearer sk-sem_…` (below).

The key acts as its owner, only in the key's projects, capped by the owner's current role: reading tools and saved-query tools work for any owner, while `query` and `ask` need an owner who can query (Editor or above). When a key reaches several projects, the assistant passes `project_id` on each call.

## Option B: Entra ID callers

For organisation-wide access without personal keys, Beacon accepts Entra access tokens on `/beacon/mcp` and maps each token to an **MCP caller** with projects and a scope. Two kinds:

- **Users**: delegated (on-behalf-of) tokens from a client app your users sign in to; optionally auto-provisioned as Beacon users.
- **Systems**: app-only tokens of a known application or service principal.

```csharp
builder.Services.AddBeaconJwtAuthentication(jwt =>
{
    jwt.EnableBearerAuthentication = true;
    jwt.Validation.JwksEndpoint = "https://login.microsoftonline.com/<tenant-id>/discovery/v2.0/keys";
    jwt.Validation.ValidIssuers = [ "https://login.microsoftonline.com/<tenant-id>/v2.0" ];
    jwt.Validation.ValidAudiences = [ "api://<beacon-app-id>" ];
});
```

```json
{
  "Beacon": {
    "Mcp": {
      "Callers": {
        "AllowedTenants": [ "<tenant-id>" ],
        "Users": {
          "Enabled": true,
          "RequiredGroups": [ "<group-object-id>" ],
          "ProjectIds": [ 4 ],
          "GroupProjects": { "<finance-group-id>": [ 7 ] },
          "DefaultRoleName": "Viewer"
        },
        "Systems": [
          { "Name": "reporting-bot", "ClientId": "<app-client-id>", "ProjectIds": [ 4 ], "Scope": "Execute" }
        ]
      }
    }
  }
}
```

| Key | Default | What it does |
|---|---|---|
| `AllowedTenants` | Required | Tenants whose tokens may map to a caller. Required once any caller is configured. |
| `Users:Enabled` | `false` | Accept delegated user tokens. |
| `Users:RequiredRoles` / `RequiredGroups` | None | The user needs one of them; with neither set, `AllowAnyTenantUser` must be `true`. |
| `Users:ProjectIds`, `Users:GroupProjects` | None | Projects every accepted user gets, plus extra projects per group or app role. |
| `Users:Scope` | `Execute` | `Read` or `Execute`. |
| `Users:AutoProvision` / `DefaultRoleName` | `true` / `Viewer` | Create a Beacon user on first call, with this role (never Admin or a functional role). |
| `Systems[]` | None | `Name`, exactly one of `ClientId` (app-only token) or `ObjectId` (service principal), `ProjectIds`, `Scope`, `HostClaims`. |

An absent `Callers` section admits no JWT caller on MCP; it fails closed. Startup validates the section, including that bearer JWT validation is on with explicit issuers and audiences. Audit rows identify Entra callers by a keyed hash (`Beacon:Mcp:CallerHashKey`, else derived from the encryption key). Full reference: [Entra ID callers](/features/mcp-entra-callers/).

## Connect a client

### Claude Code

```bash
claude mcp add --transport http beacon https://beacon.example.com/beacon/mcp \
  --header "Authorization: Bearer $BEACON_MCP_API_KEY"
```

Or share it with your team in the repository's `.mcp.json` (Claude Code expands environment variables):

```json
{
  "mcpServers": {
    "beacon": {
      "type": "http",
      "url": "https://beacon.example.com/beacon/mcp",
      "headers": { "Authorization": "Bearer ${BEACON_MCP_API_KEY}" }
    }
  }
}
```

### Cursor

`~/.cursor/mcp.json` or `.cursor/mcp.json` in the project:

```json
{
  "mcpServers": {
    "beacon": {
      "url": "https://beacon.example.com/beacon/mcp",
      "headers": { "Authorization": "Bearer sk-sem_…" }
    }
  }
}
```

### VS Code

`.vscode/mcp.json`:

```json
{
  "inputs": [
    { "type": "promptString", "id": "beacon-key", "description": "Beacon API key", "password": true }
  ],
  "servers": {
    "beacon": {
      "type": "http",
      "url": "https://beacon.example.com/beacon/mcp",
      "headers": { "Authorization": "Bearer ${input:beacon-key}" }
    }
  }
}
```

### Claude (claude.ai and Claude Desktop)

Add Beacon as a **custom connector** with the URL `https://beacon.example.com/beacon/mcp`. Connectors authenticate with OAuth, so this path needs Option B: publish your Entra authorization server and scope through `Beacon:Mcp:ProtectedResource`, register the client's redirect URI in your Entra app, and map users with `Beacon:Mcp:Callers:Users`. The step-by-step Entra setup is on [Entra ID callers](/features/mcp-entra-callers/).

Client configuration formats change; if a snippet does not work, check the client's own documentation. The endpoint, header and transport above are what Beacon needs.

## Check it works

```bash
BEACON=https://beacon.example.com
curl -s $BEACON/.well-known/mcp/server-card.json | jq '.tools[].name'

H=(-H "Authorization: Bearer $BEACON_MCP_API_KEY" -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream")

# 1. initialize: the response header Mcp-Session-Id names the session
SESSION=$(curl -s -D - -o /dev/null "${H[@]}" $BEACON/beacon/mcp \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}' \
  | awk -F': ' 'tolower($1)=="mcp-session-id"{print $2}' | tr -d '\r')

# 2. confirm initialisation
curl -s "${H[@]}" -H "Mcp-Session-Id: $SESSION" -H "MCP-Protocol-Version: 2025-06-18" $BEACON/beacon/mcp \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'

# 3. list the tools
curl -s "${H[@]}" -H "Mcp-Session-Id: $SESSION" -H "MCP-Protocol-Version: 2025-06-18" $BEACON/beacon/mcp \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
```

A client on the stateless revision skips the session: it sends `MCP-Protocol-Version: 2026-07-28` and `Mcp-Method: tools/list` headers (plus `Mcp-Name: <tool>` on `tools/call`) and puts `{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}` in `params._meta`.

Then ask the assistant something like *"Use Beacon's get_context to describe my project."*

| Symptom | Cause |
|---|---|
| 400 "A new session can only be created by an initialize request" | The client sent a request without a session: start with `initialize` (or use the stateless `2026-07-28` revision). |
| 401 with `resource_metadata` | No or invalid credential. |
| 403 at the door | The key has the Read scope, the JWT caller is not mapped, or a browser session is calling. |
| "Permission denied: this credential does not have the Execute scope" | Same, reported as a tool error. |
| "No projects are available to you." | The key or caller reaches no project. |
| "Access denied: you may read project N but not run ad-hoc queries in it." | Viewer-level access: `query`/`ask` need Query (Editor). |

## Add your own tools

- Publish an approved saved query as an MCP tool `q_<name>` without code: [Saved query tools](/features/saved-query-tools/).
- Expose endpoints of your own API as `api_<name>` tools: [Extending Beacon](/integrate/extending/#your-endpoints-as-mcp-tools).

## Governing MCP

Per-project MCP settings (row limits, read-only, PII detection, content retention, learning) live on **MCP settings** (`/beacon/mcp-settings`), editable by a SecurityAdmin and readable by an Auditor. Deployment locks (`Beacon:Mcp:ForceReadOnly`, `ForcePiiDetection`, `ForceNoContentRetention`, `ForbidRowDataToLlm`) and ceilings (`Beacon:Mcp:Ceilings`) bind them from configuration. Every call is audited; see [MCP observability & audit](/features/mcp-observability-and-audit/).
