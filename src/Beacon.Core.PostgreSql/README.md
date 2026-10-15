# Moberg.Beacon.Core.PostgreSql

Stores Beacon's own metadata in PostgreSQL. This package holds the PostgreSQL EF Core migrations and the
`UsePostgreSql` registration. It does not let Beacon query PostgreSQL as a data source; that is
`Moberg.Beacon.Connector.PostgreSql`.

## Requirements

- The `pgvector` extension. A Beacon migration runs `CREATE EXTENSION IF NOT EXISTS vector`, so either the
  migrating login can create it or it is already installed. On Azure Database for PostgreSQL Flexible Server,
  allow `VECTOR` in `azure.extensions`.
- Tables live in the fixed `beacon` schema, with snake_case names.

## Install

```bash
dotnet add package Moberg.Beacon.Core.PostgreSql
```

## Register

`UsePostgreSql` ends the `BeaconBuilder` chain, so call connectors, `ExposeDbContext` and `ExposeDocs` before it.

```csharp
using Beacon.Core;
using Beacon.Core.PostgreSql;

builder.Services
    .AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<MyBeaconScheduler>();
    })
    .UsePostgreSql(); // reads ConnectionStrings:BeaconContext

var app = builder.Build();

// Beacon does not migrate itself: create the schema if missing, then apply pending migrations.
Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true);
```

## Overloads

| Overload | Search path |
|---|---|
| `UsePostgreSql()` | Connection string from `ConnectionStrings:BeaconContext`. Beacon builds its own `NpgsqlDataSource` with `SearchPath = beacon,public`. |
| `UsePostgreSql(string connectionString, string schema = "beacon")` | Same, with the connection string passed in. |
| `UsePostgreSql(NpgsqlDataSource dataSource, string schema = "beacon")` | Your data source (for example one using Entra tokens). Beacon runs `SET search_path TO "beacon", public` on each connection it opens. |
| `UsePostgreSql(Func<IServiceProvider, NpgsqlDataSource> factory, string schema = "beacon")` | Resolved once from the root provider, e.g. `sp => sp.GetRequiredService<NpgsqlDataSource>()` for an Aspire data source. Same per-connection search path. |

`public` follows `beacon` so a pgvector already installed in `public` resolves. A host-owned data source must keep
connection reset on (no `No Reset On Close`) and must not use `Multiplexing`. It needs no `UseVector()`.

The `schema` argument and `Beacon:Schema` accept only `beacon`; any other value throws at registration.

## Documentation

- [Metadata database](https://moberghr.github.io/beacon/integrate/database/)
- [Host setup](https://moberghr.github.io/beacon/integrate/host-setup/)
- [Packages](https://moberghr.github.io/beacon/integrate/packages/) and the [integration overview](https://moberghr.github.io/beacon/integrate/)
- For coding agents: [llms-integrate.txt](https://moberghr.github.io/beacon/llms-integrate.txt)

## License

Dual-licensed under AGPL-3.0 or a commercial license. See
[LICENSING.md](https://github.com/moberghr/beacon/blob/main/LICENSING.md).
