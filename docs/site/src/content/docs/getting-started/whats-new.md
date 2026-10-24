---
title: What's new in Beacon 5.0
description: "Beacon 5.0 makes Beacon the MCP gateway of your own ASP.NET Core application, adds test and verification tooling, moves LLM providers into packages and slims the dependency graph. What is new, and the checklist for upgrading from 4.x."
---

Beacon 5.0 was released on 21 October 2026 (NuGet version `5.0.0`). Its headline is the **MCP gateway**: one package
that turns your own ASP.NET Core application into an MCP server, so Claude and other AI clients act as your signed-in
users through your own endpoints. Around it come test and verification tooling, a Claude Code plugin, LLM providers as
packages, and a lighter dependency graph.

:::tip[Upgrading with a coding agent?]
```text wrap
Upgrade this application from Beacon 4.x to 5.0.
Read https://moberghr.github.io/beacon/getting-started/whats-new.md, check this repository against every item under
"Upgrade from 4.x", apply the ones that apply, and tell me which ones did not apply and why.
Build and run the tests after the change.
```
:::

## Highlights

### MCP gateway for your own application

`Moberg.Beacon.Gateway` embeds Beacon as the MCP server of an existing application. People connect Claude to
`https://your-app/beacon/mcp` and sign in with Microsoft Entra. A tool call then runs one of your endpoints as that
person, through your own authorization, and Beacon audits it. Your Markdown documents can ground the answers.
There are no data sources and no SQL. Read-only enforcement is pinned, and so is no content retention unless you set
`RetainToolContent`. `/beacon/mcp` answers 401/403 itself and never redirects to your sign-in.

```csharp
builder.Services.AddBeaconMcpGateway(builder.Configuration, x =>
{
    x.ProjectName = "app";
    x.Database(db => db.UsePostgreSql());
    x.Entra(builder.Configuration.GetSection("AzureAd"));
    x.Users<AppMcpUsers>();                   // your IMcpHostUserResolver: who a token is
});
// …
app.UseBeaconMcpGateway();
app.MapBeaconMcpGateway();
```

An optional admin UI serves the MCP audit, the settings and your documents at `/beacon` to holders of one of your own
authorization policies, signed in through your own sign-in. → [MCP gateway for your application](/mcp/gateway/)

### Endpoint tools, ready for real applications

- `[BeaconTool]` works on handler classes whose framework copies class attributes into the endpoint, such as a Moberg
  Warp handler. On an MVC controller class it fails at startup and tells you to put the attribute on the action.
- `BindFromCaller("userId", "app_user_id")` fills the person's own id from the caller and removes the argument from the
  tool's schema. An agent cannot pass someone else's id.
- Tool calls run your `IExceptionHandler`s, so a refusal reaches the agent as your own problem details.
- `httpContext.User.IsBeaconMcpCall()` tells you a request is an agent's. You can then hold agents to less than the
  browser.
- `AddBeaconMcp(x => …)` chooses the built-in tools, turns saved-query tools off and replaces the server instructions.

→ [Host endpoint tools](/mcp/host-endpoint-tools/), recipe: [Expose an endpoint as a tool](/cookbooks/endpoint-as-tool/)

### Test it and verify it

- **`Moberg.Beacon.Testing`** has Entra-shaped test tokens, an in-memory user resolver and a real MCP client over your
  test server (`McpTestTokens`, `McpTestUsers`, `McpTestClient`). Your tests then go through the bearer middleware,
  the caller mapping, the MCP filters, the audit and your endpoint's own authorization.
- **`beacon-doctor`** (`dotnet tool install -g Moberg.Beacon.Doctor`) checks a deployed endpoint the way an MCP client
  sees it: the 401 challenge, the protected-resource metadata, the authorization server, PKCE and `offline_access`.
  With `--token` it also runs `initialize` and `tools/list`. It prints PASS/WARN/FAIL lines and exits non-zero on a
  failure.
- `src/Beacon.SampleGateway` in the repository is a reference gateway host.

### Built for coding agents

- A **Claude Code plugin** ships in the repository: `/plugin marketplace add moberghr/beacon`, then
  `/plugin install beacon@moberg-beacon`. → [Build with AI](/getting-started/with-ai/)
- **Cookbooks**: recipes that run start to finish, each with a prompt you can hand to your agent. → [Cookbooks](/cookbooks/)
- New one-file bundles: `llms-mcp.txt`, `llms-cookbooks.txt` and `llms-gateway.txt`, next to `llms-integrate.txt` and
  `llms-full.txt`.

### LLM providers as packages

`AddBeaconAI` registers OpenAI, Azure OpenAI and Anthropic (Claude). **AWS Bedrock moved to its own package**,
`Moberg.Beacon.AI.Bedrock`, which you activate with `AddBeaconBedrock()`, so `Moberg.Beacon.AI` no longer depends on
the AWS SDK. The admin settings page lists only the providers you installed
(`GET /beacon/api/admin-settings/llm-providers`). A configured provider that is not installed leaves AI without a
provider: the host logs a warning, and AI calls fail with a message that names the package to add.

**Local embeddings moved to their own package too**: `Moberg.Beacon.AI.Embeddings.Onnx`, activated with
`AddBeaconOnnxEmbeddings()`. Without it, retrieval is keyword-based and `Moberg.Beacon.AI` carries no ONNX runtime.

### Migration writers in the connectors, and a lighter build

Data-migration writes moved out of `Moberg.Beacon.Core` into the destination's connector, behind
`IMigrationBulkWriter`. PostgreSQL writes with binary `COPY`, SQL Server with `SqlBulkCopy` and `MERGE`, MySQL with
multi-row parameterised inserts, Upsert with `ON DUPLICATE KEY UPDATE`, and Truncate-Load. Every write, the clear and
the primary key lookup run on the run's one connection and transaction, so a run is all-or-nothing on PostgreSQL and
SQL Server, and an Insert or Upsert run is all-or-nothing on MySQL InnoDB. The exception is a MySQL Truncate-Load:
`TRUNCATE` commits on its own, so a run that fails after the clear leaves the table empty. `EFCore.BulkExtensions` and `Pomelo.EntityFrameworkCore.MySql`
have left the build, and `Moberg.Beacon.Core` no longer brings the SQL Server EF Core provider with it.

### Under the hood

- Beacon's admin standing comes from the stored user's live roles, not from the session's role claim (see the upgrade
  list). Demoting someone takes effect on their next request.
- `/beacon/mcp` accepts MCP credentials only: an API key or a mapped bearer token.
- Every SQL gate request now carries a row-level security context. This is groundwork: 5.0 filters no rows, and the
  lock `Beacon:RowSecurity:Enabled` stays off (a host that sets it on does not start).

## New packages

| Package | What it is |
|---|---|
| `Moberg.Beacon.Gateway` | The MCP gateway for your own application |
| `Moberg.Beacon.Testing` | Test helpers for a gateway host's MCP tools |
| `Moberg.Beacon.Doctor` | The `beacon-doctor` .NET tool |
| `Moberg.Beacon.AI.Bedrock` | The AWS Bedrock LLM provider, previously inside `Moberg.Beacon.AI` |
| `Moberg.Beacon.AI.Embeddings.Onnx` | Local ONNX embeddings for semantic retrieval, previously inside `Moberg.Beacon.AI` |

## Upgrade from 4.x

Upgrade every `Moberg.Beacon.*` package to `5.0.0` together, then go through this list. Most hosts need only the first
two items.

- [ ] **Apply the new migration.** `AddRowSecurityModel` adds five tables in the `beacon` schema and a column on
      `data_sources`, on PostgreSQL and on SQL Server. A host that calls
      `Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true)` applies it at startup. A host
      that migrates in a separate step can call `BeaconPostgreSqlMigrator.MigrateAsync(...)` or
      `BeaconSqlServerMigrator.MigrateAsync(...)` from it. → [Metadata database](/integrate/database/)
- [ ] **Bedrock users: add the package.** Reference `Moberg.Beacon.AI.Bedrock` and call `services.AddBeaconBedrock()`
      (before or after `AddBeaconAI`). Without them, Bedrock is not available and AI calls fail with
      "LLM provider Bedrock is not installed". → [AI providers](/integrate/ai/)
- [ ] **Semantic retrieval: add the embeddings package.** Reference `Moberg.Beacon.AI.Embeddings.Onnx` and call
      `services.AddBeaconOnnxEmbeddings()` (before or after `AddBeaconAI`). `AddBeaconEmbeddings()` is obsolete and no
      longer loads the model. Without the package, retrieval falls back to keywords; with
      `Beacon:Embeddings:Enabled` on, the host logs one warning the first time retrieval asks for embeddings. → [AI providers](/integrate/ai/)
- [ ] **Migration targets: register the connector.** A data-migration run writes through the destination's connector,
      so a PostgreSQL, SQL Server or MySQL target needs that connector package and its `Add<Engine>Connector()` call.
      A run into an engine without one fails before Beacon decrypts the target's credentials. If you run MySQL
      Truncate-Load jobs, keep a way to reload the table: a failed run leaves it empty.
      → [Data migration](/features/data-migration/)
- [ ] **Central package management: raise your pins.** If restore fails with `NU1109` ("Detected package downgrade"),
      raise each pinned package to the version the message names (EF Core and `Microsoft.Extensions.*` patches,
      `ClosedXML`). → [Packages](/integrate/packages/)
- [ ] **No browser sessions on `/beacon/mcp`.** A signed-in browser session, Beacon's or yours, now gets `403` there.
      MCP clients use an API key or a mapped Entra token. The MCP playground is not affected: it calls `/beacon/api`.
      → [Connect AI clients](/mcp/connect/)
- [ ] **Hosts that own sign-in: grant Admin in Beacon.** While Beacon manages users, Admin comes from the Beacon user's
      own roles. An identity-provider role named `Admin` (an Entra app role, for example) no longer makes someone
      Beacon's Admin: grant the Admin role to their Beacon user. Functional permissions mapped with
      `Beacon:Authorization:IdpRoles` are unchanged. Hosts without Beacon user management keep the claim.
      → [User management](/security/user-management/)
- [ ] **Hosts that use Core without the API or MCP packages:** the request principal must be registered. Call
      `AddBeaconApiServices`, `AddBeaconCookieAuthentication` or `AddBeaconMcp`, or register your own with
      `services.UseRequestPrincipal<T>()`. `IHttpContextAccessor` alone no longer identifies the caller.
      → [Host wiring](/integrate/host-setup/)
- [ ] **SQL Server provider no longer transitive.** `Moberg.Beacon.Core` no longer references
      `Microsoft.EntityFrameworkCore.SqlServer` or `EFCore.BulkExtensions`. A host on SQL Server already references
      `Moberg.Beacon.Core.SqlServer`. Add your own reference only if your own code used those packages through Beacon.
      The same goes for `AWSSDK.BedrockRuntime` and the ONNX packages, which `Moberg.Beacon.AI` no longer brings.
- [ ] **Code that calls Beacon's internals:**
  - `SqlGateRequest` takes a `RowSecurity` argument, which you get from `IRowSecurityResolver`.
  - The `LlmProviderFactory(accessor, logger)` constructor is obsolete. Resolve the provider registrations from DI.
  - `Constants.Migration.MaxFailedRowsBeforeStop` is gone.
  - `IMigrationBulkContextProvider`, `MigrationBulkContextFactory` and `DynamicDbContext` are removed with
    `EFCore.BulkExtensions`, and `SqlServerDataTransferManager` and `ObjectDataReader<T>` with the old SQL Server
    bulk path. Migration writes use the connectors' `IMigrationBulkWriter`.
  - `BedrockProvider` moved to the `Beacon.AI.Bedrock` assembly and `IHostEndpointDispatchMiddleware` to `Beacon.MCP`.
    Both keep their namespaces: recompile.
  - `SqlClientTransportInspector` has an abstract `Read(string)` method, so a class of your own that derives from it
    must implement it.
  - `DatabaseAuthorizationProvider`'s constructor takes `IBeaconRequestPrincipal`, and its `(IUserManagementService)`
    overload is gone.
  - `AuthenticationPathPrefixes.Matches(PathString)` is gone: use `BeaconRequestPaths.Matches`.

## Behaviour changes worth knowing

| Area | In 5.0 |
|---|---|
| MySQL migration runs | All-or-nothing on InnoDB. A run no longer ends as a partial success. A Truncate-Load that fails after the clear leaves the table empty. Upsert matches on any unique key and no longer needs the `CREATE TEMPORARY TABLES` privilege. |
| AI provider | Choosing a provider that needs an API key requires typing a key. Saving or testing a provider that is not installed is refused while AI is on. |
| Admin standing | A user who loses the Admin role loses Admin routes on their next request, not at the next session revalidation. |
| PostgreSQL and SQL Server migration runs | All-or-nothing: the writes and the key lookup share the run's transaction, with no row-by-row fallback. An Upsert into a table without a primary key fails. An unqualified SQL Server table means `dbo`. |
| SQL Server migration Insert | Keeps source identity values and NULLs (writing identity values needs `ALTER` on the table) and checks CHECK and foreign-key constraints: a violating row fails the run. |
| XLSX attachments | Dates keep a date format per cell, `DateOnly` and `DateTimeOffset` are written as dates, and a report with no rows is an empty workbook instead of an error. |

## After 5.0.0

These changes are on `main` and ship in the next release after 5.0.0.

### OpenAI and Azure OpenAI over plain HTTP

OpenAI and Azure OpenAI now go over plain HTTP, as Claude already did, so `Moberg.Beacon.AI` no longer brings the
`OpenAI`, `Azure.AI.OpenAI` or `Azure.Core` packages. Requests keep the SDKs' wire format and retry policy (up to three
retries on 408, 429 and 5xx, honouring `Retry-After` up to 60 seconds). Redirects are not followed, so an API key never
travels to a redirect target. Failures name the HTTP status and the provider's error code, never the response body, and
the request queue does not resend a call that failed with a status, since the provider already retried it.

This also fixes Azure OpenAI, which failed on every call in 4.x and 5.0.0 with a `MissingMethodException`.

When you upgrade:

- [ ] **Packages no longer transitive.** If your own code used `OpenAI`, `Azure.AI.OpenAI` or `Azure.Core` through
      Beacon, reference them yourself.
- [ ] **Code that builds providers itself:** `OpenAiProvider` and `AzureOpenAiProvider` keep their constructors but
      are obsolete. Providers are built through the `ILlmProviderRegistration`s that `AddBeaconAI()` installs, and a
      host's own registration for a built-in provider now wins whatever the call order.

### Parameter placeholders have a shape

A step parameter's placeholder must be `{identifier}`: letters, digits and underscore, starting with a letter or
underscore (so `{1}` is refused, although the editor's auto-detect accepts digits). An empty placeholder is refused, it
must be distinct within the step, and a multi-step save now also requires every placeholder to occur in its step's SQL.
Beacon checks it when a query is created or edited, when an AI actor proposes a change and when a query is newly exposed
as an MCP tool. Stored queries keep running as they are, and restoring an old version keeps its placeholders as stored,
also on an exposed tool. An already-exposed tool keeps running and can be edited or disabled; only newly exposing it
needs the shape. The next edit of a stored query asks for a valid placeholder.

When you upgrade:

- [ ] **Hosts or scripts that create queries through the API:** send `{name}` placeholders. A bare word, a quoted or
      spaced placeholder is refused with the parameter's name.

### Stronger password hashes, checked user names

Internal users' passwords are hashed at 600,000 PBKDF2 iterations, and the count is stored in the hash. Existing
hashes keep working and are upgraded at each user's next successful password sign-in
([Password hashes](/security/user-management/#password-hashes)). Creating a user, and first-run setup, now trim the user
name and e-mail address and refuse a user name outside 3 to 100 characters or with control characters, an e-mail address
that is not one plain address, and (setup) a password confirmation that differs. Editing a user keeps a value sent back
exactly as stored, and trims and checks only the fields that change.

When you upgrade:

- [ ] **Rollback:** an earlier version cannot read an upgraded hash. After a rollback, reset the passwords of internal
      users who signed in on this version.
- [ ] **Scripts that create users through the API:** send a user name of at least 3 characters and a plain e-mail
      address (or none).

## Release notes

The full list of changes is on the [5.0.0.0 release](https://github.com/moberghr/beacon/releases/tag/5.0.0.0).
