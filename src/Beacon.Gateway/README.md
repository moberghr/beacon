# Moberg.Beacon.Gateway

Turn an existing ASP.NET Core application into an **MCP server for its own users**. People connect Claude (claude.ai,
Claude Desktop, Cowork, Claude Code) to `https://your-app/beacon/mcp` and sign in with Microsoft Entra. Claude then
calls a set of your existing endpoints **as that person**, through your own authorization, and every call is
audited. Your Markdown documents can ground the answers. There are no data sources and no SQL, and you write no
OAuth server.

**Docs:** [The gateway recipe](https://moberghr.github.io/beacon/mcp/gateway/) ·
[Host endpoint tools](https://moberghr.github.io/beacon/mcp/host-endpoint-tools/) ·
[Expose an endpoint as a tool](https://moberghr.github.io/beacon/cookbooks/endpoint-as-tool/) ·
for agents: [`llms-gateway.txt`](https://moberghr.github.io/beacon/llms-gateway.txt)

## Install

```bash
dotnet add package Moberg.Beacon.Gateway
dotnet add package Moberg.Beacon.Core.PostgreSql      # or Moberg.Beacon.Core.SqlServer, for Beacon's own tables
dotnet add package Moberg.Beacon.Testing              # in your test project
```

`Beacon:EncryptionKey` is required: base64 of 32 random bytes (`openssl rand -base64 32`) from user secrets or your
secret store. Set `Beacon:PublicBaseUrl` to the URL clients use.

## Wire it

```csharp
builder.Services.AddBeaconMcpGateway(builder.Configuration, x =>
{
    x.ProjectName = "app";
    x.Database(db => db.UsePostgreSql(sp => sp.GetRequiredService<NpgsqlDataSource>()));
    x.Entra(builder.Configuration.GetSection("AzureAd"));         // TenantId, ClientId
    x.Users<AppMcpUsers>();                                         // your IMcpHostUserResolver
    x.Tools(y => y.BindFromCaller("userId", "app_user_id"));       // the person's own id, never the agent's
    x.Docs(y => y.FromEmbeddedResources(typeof(Program).Assembly, "app-docs/"));   // optional grounding
});

var app = builder.Build();
app.UseBeaconMcpGateway();          // before UseAuthentication
// … UseStaticFiles, UseAuthentication, UseAuthorization …
app.MapBeaconMcpGateway();          // /beacon/mcp and the discovery documents
```

`AddBeaconMcpGateway` is a profile of Beacon's host chain (`AddBeacon(...).AddAI().AddApi().AddMcp()`) with your
application owning sign-in. The package's `UseBeaconPipeline()` and `MapBeacon()` serve a full Beacon host instead and
throw on a gateway host.

`IMcpHostUserResolver` maps the Entra token (by object id, `oid`) to exactly one active user of your application, or to
nobody. It never creates users. It runs on every call, so deactivating someone cuts MCP off at their next call.

## Mark endpoints as tools

```csharp
app.MapGet("/api/my/orders", (int userId, OrderService orders) => orders.Of(userId))
    .RequireAuthorization()
    .WithBeaconTool("my_orders", "The signed-in user's orders, newest first: id, placedAt (UTC), status, total.", readOnly: true);
```

A tool is self-service: it reads or changes the person's own records, has authorization metadata, declares
`ReadOnly`, and takes the person's id from the caller. Write tools need `AllowWriteTools` and make clients ask before
each call. `principal.IsBeaconMcpCall()` tells your own permission checks that a request is an agent's.

## Migrate, register, test, verify

- **Schema:** `BeaconPostgreSqlMigrator.MigrateAsync(...)` or `BeaconSqlServerMigrator.MigrateAsync(...)` from your
  migration step (schema `beacon`).
- **Entra:** v2 tokens, the exact MCP URL in `identifierUris`, a delegated `mcp.access` scope, and a client
  registration for Claude with `offline_access`. See the
  [checklist](https://moberghr.github.io/beacon/mcp/gateway/#6-entra-app-registration).
- **Test:** `Moberg.Beacon.Testing` gives Entra-shaped test tokens, test users and a real MCP client over your test
  server (`McpTestTokens`, `McpTestUsers`, `McpTestClient`).
- **Verify:** `dotnet tool install -g Moberg.Beacon.Doctor`, then
  `beacon-doctor mcp https://your-app/beacon/mcp` walks the sign-in path the way Claude does.

## Let your coding agent do it

```text
Add an MCP gateway to this ASP.NET Core app with Beacon.
Read https://moberghr.github.io/beacon/llms-gateway.txt first and follow it step by step.
Ask me before every decision it marks as one.
```

With Claude Code: `/plugin marketplace add moberghr/beacon`, then `/plugin install beacon@moberg-beacon`, and ask for
an MCP gateway.

## License

Dual-licensed under AGPL-3.0 or a commercial license. See
[LICENSING.md](https://github.com/moberghr/beacon/blob/main/LICENSING.md).
