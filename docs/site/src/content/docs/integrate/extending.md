---
title: Extending Beacon
description: "Make your own application part of Beacon: expose your EF Core DbContext as a read-only data source, import your Markdown docs as grounding, turn your HTTP endpoints into MCP tools, and replace Beacon's extension points."
---

An embedded Beacon can know your application, not only your databases. Three host extensions, all opt-in and all scoped to one **host project**:

| Extension | You get | Page |
|---|---|---|
| `ExposeDbContext<TContext>` | Your EF Core model as a read-only data source, with a table allow-list and column masking | [Host DbContext](/features/host-dbcontext/) |
| `ExposeDocs` | Your Markdown documentation imported as grounding for `ask`, `search` and `get_documentation` | [Host docs](/features/host-docs/) |
| `AddHostEndpointTools` | Your HTTP endpoints as MCP tools `api_<name>`, dispatched in-process through your own authorization | [Host endpoint tools](/features/host-endpoint-tools/) |

## Your DbContext as a data source

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options => { /* … */ })
    .ExposeDbContext<ShopDbContext>(x =>
    {
        x.ProjectName = "Shop";                            // the host project; defaults to the data-source name
        x.ReadOnlyConnection("ShopReadOnly")               // ConnectionStrings:ShopReadOnly, a read-only login
            .AllowTables("Orders", "OrderLines", "Customers")
            .AllowEntity<Product>()
            .ExcludeColumns(y => y.Table == "Customers" && y.Name == "Notes")
            .MaskColumns(y => y.Name is "Email" or "Phone");
    })
    .AddSqlServerConnector()
    .UseSqlServer();                                       // ends the chain: ExposeDbContext comes first
```

- Tables are **default-deny**; unknown names fail startup.
- Columns whose names look like secrets (`password`, `token`, `apikey`, `hash`, `otp`, …) are always excluded unless you lift one explicitly with `IncludeSecretLikeColumn("Table.Column")`.
- The connection string is **never stored**. Beacon keeps only its name and reads it from configuration when it connects.
- SQL Server and PostgreSQL models are supported.
- Every statement is checked against the exposed slice in the execution gate and again in the provider; masked columns are masked by output position.

## Your docs as grounding

```csharp
    .ExposeDocs(x =>
    {
        x.ProjectName = "Shop";
        x.FromDirectory("docs", "**/*.md").Exclude("drafts/**");
        // x.FromEmbeddedResources(typeof(Program).Assembly, "Shop.Docs.");
        // x.MaxDocumentBytes = 512 * 1024;
    })
```

`.md`, `.markdown` and `.txt` files are imported into the project, chunked and embedded. MCP clients read them through `search` (`[IMPORTED_DOC]` results), `get_documentation document=…` and `get_context format=agents_md`.

## Sync at startup

Both extensions need one startup call, **after** Beacon's schema is migrated:

```csharp
var app = builder.Build();
Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true);
await app.Services.SyncBeaconHostAsync();   // data sources first, then docs; idempotent; replica-safe lock
```

`SyncBeaconHostDataSourcesAsync()` syncs the data sources only.

## Your endpoints as MCP tools

```csharp
builder.Services.AddBeaconMcp()
    .AddHostEndpointTools(x =>
    {
        x.ProjectName = "Shop";       // the host project the tools belong to
        // x.AllowWriteTools = true;  // off by default
    });

app.MapGet("/api/orders/{id:int}", (int id, OrderService orders) => orders.Find(id))
    .RequireAuthorization("ViewOrders")
    .WithBeaconTool("order_by_id", "One order by id.", readOnly: true);
```

Or on a controller action, or on a handler class whose framework copies class attributes into its endpoint (a Moberg Warp handler): `[BeaconTool("customer_orders", Description = "…", ReadOnly = true)]`.

- `ReadOnly` must be declared explicitly; Beacon never infers it from the HTTP verb.
- The endpoint must carry authorization metadata (or your host has a fallback policy); otherwise startup refuses it.
- Calls run in-process through your endpoint's own pipeline, as a host principal built for the MCP caller (`IMcpHostPrincipalFactory`).
- Only mapped Entra callers (users or systems) of the host project can call them; API-key callers cannot.
- Write tools (`ReadOnly = false`) need `AllowWriteTools`, are offered to **user** callers only and are audited as `api_<name>:write`.
- Above 20 tools (`NamedToolLimit`), clients see `search_api` + `call_api` instead of one tool per endpoint.
- An argument that is the caller's own id in your application is bound from the caller with `x.BindFromCaller("userId", "<claim type>")`: it leaves the tool's schema and the call carries the caller's value. See [Arguments the caller fills](/features/host-endpoint-tools/#arguments-the-caller-fills).
- An exception your endpoint throws runs your `IExceptionHandler`s, so a refusal reaches the agent as the status and problem a browser would get.

## Saved queries as tools without code

A SecurityAdmin can publish an approved saved query as an MCP tool `q_<name>` from the query's page. See [Saved query tools](/features/saved-query-tools/).

## Replaceable services

Beacon registers most services with `TryAdd`, so **your registration wins if you register first** (before `AddBeaconServices`, `AddBeaconApiServices` or `AddHostEndpointTools`, depending on who registers it).

| Interface | Default | Replace it to |
|---|---|---|
| `IBeaconScheduler` | None (required) | Use your job runner. |
| `IEmailAdapter` | none | Send email (`AddEmailAdapter<T>()`). |
| `IBeaconAuthenticationProvider` | user store | Check passwords somewhere else. |
| `IBeaconAuthorizationProvider` | user store / allow-all | Cap users' global read/write from your own model. |
| `IProjectMembershipSource` | Beacon's membership table | Take project membership from your application. Beacon's membership endpoints then answer 409. |
| `IMcpCallerMapper` | `Beacon:Mcp:Callers` | Map JWT callers to projects and scopes your own way. |
| `IMcpHostPrincipalFactory` | copies name/email/oid; system callers get `HostClaims` | Build the principal your endpoints expect for MCP calls. |
| `IHostEndpointDispatchMiddleware` | none | Run your own middleware logic (tenant, culture, audit) around dispatched tool calls. Ships in `Moberg.Beacon.MCP` (namespace `Beacon.Core.HostEndpoints`). |
| `IApprovalNotifier` | SignalR | Notify approvers elsewhere. |
| `IBeaconUserContext` | `HttpContextUserContext` (Beacon.Api) | Resolve the current user differently. Replace it in both places it is registered. |
| `IBeaconRequestPrincipal` | Core: no request; Beacon.Api and Beacon.MCP: the current `HttpContext` | Tell Beacon's core who the request's caller is from your own request model. Call `UseRequestPrincipal<T>()` before or after Beacon's registrations: it replaces Beacon's own principals (Core's default and the `HttpContext` one) in any order, and Beacon's web registrations after it keep yours. A principal registered with `AddSingleton` before Beacon's registrations stays, and after them wins as the last registration. A `TryAdd*` after `AddBeaconServices` is a no-op, because Core has already registered its default. A host that maps Beacon's endpoints over Core's default fails to start. |

`IProjectAccessEvaluator` is replaceable in principle but it is the single decider for project access; replace it only with great care, and keep the project-authorization tests green if you fork it.
