# Moberg.Beacon.Core.SqlServer

Stores Beacon's own metadata in SQL Server. This package holds the SQL Server EF Core migrations and the
`UseSqlServer` registration. It does not let Beacon query SQL Server as a data source; that is
`Moberg.Beacon.Connector.SqlServer`.

Tables live in the fixed `beacon` schema. There is no pgvector column on SQL Server: semantic retrieval computes
similarity over the stored embedding bytes instead of a vector index.

## Install

```bash
dotnet add package Moberg.Beacon.Core.SqlServer
```

## Register

`UseSqlServer` ends the `BeaconBuilder` chain, so call connectors, `ExposeDbContext` and `ExposeDocs` before it.

```csharp
using Beacon.Core;
using Beacon.Core.SqlServer;

builder.Services
    .AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<MyBeaconScheduler>();
    })
    .UseSqlServer(); // reads ConnectionStrings:BeaconContext

var app = builder.Build();

// Beacon does not migrate itself: create the schema if missing, then apply pending migrations.
Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true);
```

| Overload | What it does |
|---|---|
| `UseSqlServer()` | Connection string from `ConnectionStrings:BeaconContext`. |
| `UseSqlServer(string connectionString)` | Same, with the connection string passed in. |

The schema is always `beacon`: `Beacon:Schema` and the obsolete `schema` overloads (removed in 6.0) accept only `beacon`;
any other value throws at registration. With
`createSchema: true`, `UseBeacon` creates the `beacon` schema when it does not exist. The migration history table
is `beacon.__EFMigrationsHistory`.

## Documentation

- [Metadata database](https://moberghr.github.io/beacon/integrate/database/)
- [Host setup](https://moberghr.github.io/beacon/integrate/host-setup/)
- [Packages](https://moberghr.github.io/beacon/integrate/packages/) and the [integration overview](https://moberghr.github.io/beacon/integrate/)
- For coding agents: [llms-integrate.txt](https://moberghr.github.io/beacon/llms-integrate.txt)

## License

Dual-licensed under AGPL-3.0 or a commercial license. See
[LICENSING.md](https://github.com/moberghr/beacon/blob/main/LICENSING.md).
