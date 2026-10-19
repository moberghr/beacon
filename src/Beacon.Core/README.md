# Moberg.Beacon.Core

The core of Beacon: the domain model, the MediatR handlers, the services and the EF Core model for Beacon's own
metadata database. Every other Beacon package depends on it.

Core does not choose a database. Add one provider package to store Beacon's data:

- [`Moberg.Beacon.Core.PostgreSql`](https://www.nuget.org/packages/Moberg.Beacon.Core.PostgreSql) (PostgreSQL with pgvector)
- [`Moberg.Beacon.Core.SqlServer`](https://www.nuget.org/packages/Moberg.Beacon.Core.SqlServer)

Add `Moberg.Beacon.Connector.*` packages for the engines Beacon queries, `Moberg.Beacon.Api` for the REST API and
sign-in, `Moberg.Beacon.UI` for the web app, and `Moberg.Beacon.AI` / `Moberg.Beacon.MCP` for the AI layer and the
MCP server.

## Install

```bash
dotnet add package Moberg.Beacon.Core
```

A provider package brings Core in as a dependency, so you rarely add it directly.

## Register

`AddBeaconServices` returns a `BeaconBuilder`. Chain connectors, `ExposeDbContext` and `ExposeDocs` on it first.
`UsePostgreSql()` or `UseSqlServer()` comes last: it returns `IServiceCollection` and ends the chain.

```csharp
using Beacon.Connector.PostgreSql;
using Beacon.Core;
using Beacon.Core.PostgreSql;

builder.Services
    .AddBeaconServices(builder.Configuration, options =>
    {
        // Required: your IBeaconScheduler implementation (namespace Beacon.Core.Worker).
        options.AddBeaconScheduler<MyBeaconScheduler>();
        // Used for links in notifications; include the /beacon path.
        options.BaseUrl = "https://beacon.example.com/beacon";
    })
    .AddPostgreSqlConnector()
    .UsePostgreSql();

var app = builder.Build();

// Beacon does not migrate itself. Create the beacon schema if needed and apply pending migrations.
Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true);
```

`AddBeaconServices` throws when no scheduler is set, or when `Beacon:EncryptionKey` is missing or weak. The key is
base64 of at least 32 random bytes (for example `openssl rand -base64 32`). Keep it in user secrets, an
environment variable (`Beacon__EncryptionKey`) or a secret store, never in a committed file.

| Key | Default | What it does |
|---|---|---|
| `ConnectionStrings:BeaconContext` | none | Beacon's metadata database. Read by the parameterless `UsePostgreSql()` / `UseSqlServer()`. |
| `Beacon:EncryptionKey` | none | Encrypts stored connection strings and secrets. Required. |
| `Beacon:Schema` | `beacon` | Leave unset or set to `beacon`. Any other value fails startup. |

The web app is served under `/beacon`, the REST API under `/beacon/api` and the MCP server at `/beacon/mcp`. Those
come from the Api, UI and MCP packages, not from Core. Core does not reference ASP.NET Core's HTTP abstractions: it reads
the request's caller through `IBeaconRequestPrincipal`, which the Api and MCP packages register; without them every
scope is off-request.

## Documentation

- [Integrate Beacon](https://moberghr.github.io/beacon/integrate/)
- [Packages](https://moberghr.github.io/beacon/integrate/packages/)
- [Host setup](https://moberghr.github.io/beacon/integrate/host-setup/)
- [Metadata database](https://moberghr.github.io/beacon/integrate/database/)
- For coding agents: [llms-integrate.txt](https://moberghr.github.io/beacon/llms-integrate.txt)

## License

Dual-licensed under AGPL-3.0 or a commercial license. See
[LICENSING.md](https://github.com/moberghr/beacon/blob/main/LICENSING.md).
