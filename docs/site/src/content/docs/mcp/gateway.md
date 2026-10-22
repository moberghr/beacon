---
title: MCP gateway for your application
description: "Give AI clients (Claude, Claude Desktop, Cowork, Claude Code) governed access to your own ASP.NET Core application: your users sign in with Entra, your endpoints become tools that run as them, your docs become grounding. One package, about thirty lines, no data sources."
---

This page is the whole recipe for one use case: **an MCP server for your own application.** People connect Claude to
`https://your-app/beacon/mcp`, sign in with Microsoft Entra, and Claude calls a set of your existing endpoints **as that
person**, through your own authorization, with every call audited. Beacon's data product (data sources, SQL, saved queries,
subscriptions) is not part of it. If that is what you want, follow [Integrate Beacon](/integrate/) instead.

:::tip[Using Claude Code or another coding agent?]
With Claude Code, install Beacon's plugin: `/plugin marketplace add moberghr/beacon`, then
`/plugin install beacon@moberg-beacon`. Its skills (`beacon-gateway`, `beacon-tool`, `beacon-entra`) follow this guide.
With any agent, point it at this guide in one file:

```text wrap
Add an MCP gateway to this ASP.NET Core app with Beacon.
Read https://moberghr.github.io/beacon/llms-gateway.txt first and follow it step by step.
Ask me before every decision it marks as one.
```
:::

## Decisions to make first

An agent should ask these, not guess.

| Decision | Options | Default |
|---|---|---|
| Who may connect | Every active user of your application, or a subset | Every active user your users table knows |
| What a tool call may do | Self-service only (the person's own records), or what the person may do in the browser | **Self-service only**: an agent reads other people's free text and sends what it reads to a model |
| Which endpoints become tools | Any endpoint with authorization metadata | Reads of the person's own data, plus writes the person would do themselves |
| Admin UI for the audit and settings | On (behind one of your policies) or off | On |
| Beacon's database | PostgreSQL (with pgvector available) or SQL Server; the app's database in schema `beacon`, or a separate one | The app's own database, schema `beacon` |

## 1. Packages and secrets

```bash
dotnet add package Moberg.Beacon.Gateway
dotnet add package Moberg.Beacon.Core.PostgreSql      # or Moberg.Beacon.Core.SqlServer
dotnet add package Moberg.Beacon.Testing              # in the test project
```

`Beacon:EncryptionKey` is required: base64 of 32 random bytes (`openssl rand -base64 32`), from user secrets or your secret
store, never a committed file. It keys the caller hashes in the audit. Set `Beacon:PublicBaseUrl` to the URL clients use
(`https://your-app`): the discovery documents and challenges advertise `<PublicBaseUrl>/beacon/mcp`.

## 2. Who a token is: `IMcpHostUserResolver`

Beacon validates the Entra token (tenant, audience, delegated, scope, client). Your application decides who the person
is and what a tool call carries:

```csharp
internal sealed class AppMcpUsers(AppDbContext db) : IMcpHostUserResolver
{
    public async Task<McpHostUser?> FindAsync(McpTokenIdentity token, CancellationToken ct)
    {
        var users = await db.Users
            .Where(x => x.ObjectId == token.ObjectId)
            .Where(x => x.IsActive)
            .Select(x => new { x.Id, x.DisplayName })
            .Take(2)
            .ToListAsync(ct);

        // Exactly one, or nobody: an object id two active users carry does not say which of them this is.
        if (users.Count != 1)
        {
            return null;
        }

        return new McpHostUser(
            users[0].Id.ToString(),
            users[0].DisplayName,
            [new Claim("app_user_id", users[0].Id.ToString())]);   // the claims your own sign-in adds
    }
}
```

- Look users up by `ObjectId` (`oid`), never by `sub`: the token was issued to another client, so its `sub` differs.
- Never create or link a user here. Someone who has never signed in to your application is refused.
- It runs on every tool call, so deactivating a person in your application cuts MCP off at their next call.

## 3. Program.cs

```csharp
// Register any IClaimsTransformation of your own BEFORE the gateway (it runs yours first).
builder.Services.AddBeaconMcpGateway(builder.Configuration, x =>
{
    x.ProjectName = "app";                                                        // Beacon's project for your tools and docs
    x.Database(db => db.UsePostgreSql(sp => sp.GetRequiredService<NpgsqlDataSource>()));  // or db.UsePostgreSql() with ConnectionStrings:BeaconContext
    x.Entra(builder.Configuration.GetSection("AzureAd"))                          // TenantId, ClientId (Microsoft.Identity.Web's section)
        .AllowClients(builder.Configuration.GetSection("McpAccess:AllowedClientIds"));  // optional: only Claude's client
    x.Users<AppMcpUsers>();
    x.Tools(y =>
    {
        y.AllowWriteTools = true;                                                 // only if you mark write tools
        y.BindFromCaller("userId", "app_user_id");                                // the person's own id, never the agent's
    });
    x.Docs(y => y.FromEmbeddedResources(typeof(Program).Assembly, "app-docs/"));   // optional grounding
    x.AdminUi(y =>                                                                // optional
    {
        y.HostPolicy = "beacon.admin";                                            // one of YOUR authorization policies
        y.HostUserId = z => z.FindFirst("app_user_id")?.Value;
        y.SignInPath = "/Account/SignIn";
    });
});

var app = builder.Build();
app.UseBeaconMcpGateway();          // before UseAuthentication
// ... UseStaticFiles (the admin UI's assets), UseAuthentication, UseAuthorization ...
app.MapBeaconMcpGateway();          // /beacon/mcp, discovery documents, the admin UI
```

What you get, and what you do not have to write:

- **Callers:** delegated tokens of your tenant and registration with the `mcp.access` scope (`RequireScope` changes it),
  whose person your resolver knows. App-only tokens, ID tokens, other tenants and other scopes are refused. Entra roles and
  groups are never read.
- **Tools:** your marked endpoints as `api_<name>`, plus `get_context`, `search` and `get_documentation` for your docs. No
  SQL tools, no saved-query tools, no data sources. Each tool's description ends with a note that it runs with the
  caller's permissions in your application; when your endpoints run with less (self-service only, say), word it yourself
  with `y.DescriptionNote` in `x.Tools(...)` (see [Host endpoint tools](/mcp/host-endpoint-tools/)).
- **Locks:** read-only and no content retention are pinned in code (`RetainToolContent = true` keeps call content).
- **Your session never counts on `/beacon/mcp`,** and a bearer token never counts on your routes. The MCP route answers
  401/403 itself, never your sign-in redirect: you edit no cookie events.
- **Discovery:** `/.well-known/oauth-protected-resource` publishes your tenant's v2 issuer and
  `api://<ClientId>/mcp.access` plus `offline_access` (so clients refresh instead of re-prompting hourly). It names the
  server `Beacon MCP` and links Beacon's docs unless you set `Beacon:Mcp:ProtectedResource:ResourceName` and
  `ResourceDocumentation` to your application's.
- **The host project** is synced the first time it is needed; the `beacon-mcp-gateway` health check reports Degraded until
  then. No hosted service, no startup step.
- **Admin UI** at `/beacon`: the MCP audit, the MCP settings, the learning views and your project, for holders of
  `HostPolicy`, signed in with your own sign-in. Every other Beacon endpoint answers 404.

## 4. Migrations

Beacon's tables live in schema `beacon`. Apply them from your migration step, not from every web replica:

```csharp
await Beacon.Core.PostgreSql.BeaconPostgreSqlMigrator.MigrateAsync(dataSource);   // or (connectionString)
await Beacon.Core.SqlServer.BeaconSqlServerMigrator.MigrateAsync(connectionString);
```

On PostgreSQL one migration creates the `vector` extension: allow it (Azure Database for PostgreSQL: add `VECTOR` to
`azure.extensions`, which replaces the whole list). Sharing your `NpgsqlDataSource` needs connection reset on and
multiplexing off; Beacon refuses one that does either.

## 5. Mark endpoints as tools

On a minimal API endpoint (`using Beacon.MCP.HostEndpoints;` for `WithBeaconTool`), a controller action, or a handler class
whose framework copies class attributes into the endpoint (a Moberg Warp handler; `using Beacon.Core.HostEndpoints;` for
`[BeaconTool]`):

```csharp
app.MapGet("/api/my/orders", (int userId, OrderService orders) => orders.Of(userId))
    .RequireAuthorization()
    .WithBeaconTool("my_orders", "The signed-in user's orders, newest first: id, placedAt (UTC), status, total.", readOnly: true);

[Authorize]
[BeaconTool("request_leave", Description = "...", ReadOnly = false, Destructive = false, Idempotent = false)]
[WarpHttpPost("/api/time-off/requests")]
public sealed class RequestLeaveCommandHandler(...) { ... }
```

The rules an agent follows for every tool:

1. **Self-service.** A tool reads or changes the person's own records. Never mark an admin endpoint or one gated by a
   permission a person might hold over others.
2. **Data minimisation.** If an endpoint returns other people's data to some callers (a manager's view, a list by
   sequential id), do not mark it: add a `/api/my/...` sibling that filters on the caller and takes no subject.
3. **Authorization metadata.** The endpoint needs `[Authorize]` or a policy; startup refuses one without (unless you have a
   fallback policy). The call runs through the endpoint's own authorization as the user your resolver returned.
4. **The person's id is bound, not asked.** `BindFromCaller("userId", "app_user_id")` takes `userId` out of the tool's
   schema and fills it from the caller; an agent sending it is refused. A name no tool takes is warned at startup.
5. **Descriptions are for the agent:** what comes back, field by field; what each argument means; every refusal; for a
   write tool, "call it only when the person asked for this change". Beacon appends the route and the read/write flag.
6. **`ReadOnly` is always declared.** A write tool (`ReadOnly = false`) needs `AllowWriteTools`, is offered to signed-in
   people only, makes clients ask before each call, and is audited as `api_<name>:write`.

### Recognising an agent call

A tool call's principal has the authentication type `BeaconMcpUser`. Use it to hold agents to less than the browser —
self-service only, whatever the person may do there:

```csharp
if (httpContext.User.IsBeaconMcpCall())
{
    // no permissions, no manager grants: the person's own records only
}
```

### Refusals reach the agent as your problem details

Tool calls run your `IExceptionHandler`s, as `UseExceptionHandler()` does for the browser: a refusal your application maps
to a 403/404/400 problem reaches the agent as that status and body. An exception no handler takes is a generic error.

## 6. Entra app registration

On **your application's existing registration** (the `AzureAd:ClientId` one):

- [ ] Manifest: `api.requestedAccessTokenVersion = 2`. First — Entra only accepts an HTTPS Application ID URI on a v2 app.
- [ ] Expose an API: keep `api://<clientId>` and add the exact MCP URL `https://<your-domain>/beacon/mcp` to
      `identifierUris`. Claude sends it as the `resource`; a mismatch fails with **`AADSTS9010010`**. The domain must be a
      verified domain of the tenant (`*.azurecontainerapps.io` and similar cannot be).
- [ ] Expose an API: add the delegated scope **`mcp.access`**.

**A client registration for Claude** (Entra has no dynamic client registration):

- [ ] New app registration, Web platform, redirect URI `https://claude.ai/api/mcp/auth_callback`.
- [ ] A client secret (record its expiry: the connector stops when it lapses).
- [ ] API permissions: your API's `mcp.access` (delegated) and `offline_access`; grant admin consent.
- [ ] Give the client id and secret to whoever adds the connector in Claude; optionally list the client id in
      `McpAccess:AllowedClientIds`.

## 7. Test it

```csharp
var tokens = new McpTestTokens(tenantId, clientId);
var users = new McpTestUsers().Add(objectId: "oid-1", id: "42", displayName: "Ada", new Claim("app_user_id", "42"));

builder.Services.TrustMcpTestTokens(tokens);   // after AddBeaconMcpGateway, in the test host only
builder.Services.UseMcpTestUsers(users);

await using var client = await McpTestClient.ConnectAsync(app.GetTestClient(), tokens.For("oid-1"));
var tools = await client.ListToolsAsync();
var result = await client.CallToolAsync("api_my_orders");
```

For trying the gateway by hand on a laptop without Entra, run a local OIDC mock (for example
`ghcr.io/navikt/mock-oauth2-server`) that mints delegated tokens with your tenant id, audience, an `oid` and
`scp=mcp.access`, and point `AzureAd:Instance`/`TenantId` validation at it in Development only.

The client is the official MCP SDK's, so a test goes through the bearer middleware, the caller mapping, the route policy,
the MCP filters and audit, and your endpoint's own authorization. Listing and calling tools needs Beacon's database (project
access and the audit live there): run those tests against a real PostgreSQL or SQL Server with Beacon's schema migrated.

Pin, in your own tests: exactly which endpoints are tools (and which write), that each refuses somebody else's data, and
that an agent call is self-service only.

## 8. Verify a deployment

`beacon-doctor` walks the sign-in path the way an MCP client does and prints PASS/WARN/FAIL (exit code 1 on a failure):

```bash
dotnet tool install -g Moberg.Beacon.Doctor
beacon-doctor mcp https://your-app/beacon/mcp                    # the challenge, discovery, Entra metadata, absent documents
beacon-doctor mcp https://your-app/beacon/mcp --token "$TOKEN"   # plus initialize and tools/list as that person
```

By hand:

- [ ] `POST /beacon/mcp` without a token → 401 with `WWW-Authenticate: Bearer resource_metadata=...`.
- [ ] `/.well-known/oauth-protected-resource` → your v2 issuer, `api://<clientId>/mcp.access`, `offline_access`, and a
      `resource` equal, character for character, to an `identifierUris` entry.
- [ ] `/health` → `beacon-mcp-gateway` Healthy.
- [ ] Claude connects, lists your tools, a read answers, and a write tool asks before it runs.
- [ ] After an hour the connector refreshes silently.

| Symptom | Cause |
|---|---|
| `AADSTS9010010` | The MCP URL is not an `identifierUris` entry (exactly), or the domain is not verified. |
| 401 `invalid_token` | Wrong issuer or audience: check `AzureAd:TenantId` / `ClientId`, and that the token is v2. |
| 403 at `/beacon/mcp` | The token is valid but not a caller: wrong scope or client, the person unknown to your resolver, or the host project not synced yet. The log has the reason with the caller hash. |
| 503 with `Retry-After` | Entra's signing keys could not be fetched. |
| Re-prompted to sign in every hour | `offline_access` missing from `scopes_supported` or not consented. |
| A tool error "failed with an unexpected error" | The endpoint threw and no `IExceptionHandler` took it. |
| Startup: "relies on '…' being its only authentication scheme" | Name your default: `AddAuthentication("YourScheme")`. |
| Startup: "Register your IClaimsTransformation before calling AddBeaconMcpGateway" | Move your registration above the gateway's. |
