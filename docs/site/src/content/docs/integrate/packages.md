---
title: Packages
description: Every Moberg.Beacon NuGet package, what it contains, which ones you need, and how versions are published.
---

Beacon is published to nuget.org as `Moberg.Beacon.*` packages. Package IDs carry the `Moberg.` prefix; assemblies and namespaces stay `Beacon.*` (`using Beacon.Core;`). Every package targets `net10.0`.

## Which packages you need

| Package | Contains | Needed when |
|---|---|---|
| `Moberg.Beacon.Core` | Domain, MediatR handlers, EF model, project access, SQL execution gate, notifications, scheduling contracts | Always (every other package depends on it) |
| `Moberg.Beacon.Core.PostgreSql` | Beacon's metadata database on PostgreSQL: `UsePostgreSql(...)`, migrations, pgvector support | Beacon's own data lives in PostgreSQL |
| `Moberg.Beacon.Core.SqlServer` | Beacon's metadata database on SQL Server: `UseSqlServer(...)`, migrations | Beacon's own data lives in SQL Server |
| `Moberg.Beacon.Api` | REST endpoints under `/beacon/api`, the SignalR hub, cookie/OIDC/JWT/API-key authentication, login and first-run setup endpoints, antiforgery, Data Protection helpers | You serve the UI or the REST API — in practice always |
| `Moberg.Beacon.UI` | The built React app as a Razor Class Library, served at `/beacon` | You want the UI |
| `Moberg.Beacon.AI` | LLM providers, the request queue, documentation generation, grounding, embeddings, learning loop, AI actors | AI features, and always with MCP (MCP depends on it) |
| `Moberg.Beacon.MCP` | The MCP server at `/beacon/mcp`, its tools, filters, discovery documents, host endpoint tools | You expose Beacon to AI clients |
| `Moberg.Beacon.Connector.PostgreSql` | PostgreSQL connector | You monitor PostgreSQL |
| `Moberg.Beacon.Connector.SqlServer` | SQL Server connector | You monitor SQL Server |
| `Moberg.Beacon.Connector.MySql` | MySQL connector (also the MySQL migration-write path) | You monitor MySQL |
| `Moberg.Beacon.Connector.AzureSynapse` | Azure Synapse connector | You monitor Synapse |
| `Moberg.Beacon.Connector.Snowflake` | Snowflake connector | You monitor Snowflake |
| `Moberg.Beacon.Connector.BigQuery` | Google BigQuery connector | You monitor BigQuery |
| `Moberg.Beacon.Connector.Databricks` | Databricks SQL connector | You monitor Databricks |
| `Moberg.Beacon.Connector.CloudWatch` | AWS CloudWatch Logs Insights connector | You monitor CloudWatch |
| `Moberg.Beacon.Connector.Api` | REST API connector (OpenAPI import, JSON to rows) | You monitor HTTP APIs |

:::caution[Two different "SQL Server" packages]
`Moberg.Beacon.Core.SqlServer` stores **Beacon's own data** in SQL Server (`UseSqlServer`). `Moberg.Beacon.Connector.SqlServer` lets Beacon **monitor** a SQL Server database (`AddSqlServerConnector`). If both are true, add both. The same split applies to PostgreSQL.
:::

## Typical sets

**PostgreSQL metadata, monitoring PostgreSQL, with UI and MCP:**

```bash
dotnet add package Moberg.Beacon.Core
dotnet add package Moberg.Beacon.Core.PostgreSql
dotnet add package Moberg.Beacon.Api
dotnet add package Moberg.Beacon.UI
dotnet add package Moberg.Beacon.AI
dotnet add package Moberg.Beacon.MCP
dotnet add package Moberg.Beacon.Connector.PostgreSql
```

**SQL Server shop, no AI, no MCP:**

```bash
dotnet add package Moberg.Beacon.Core
dotnet add package Moberg.Beacon.Core.SqlServer
dotnet add package Moberg.Beacon.Api
dotnet add package Moberg.Beacon.UI
dotnet add package Moberg.Beacon.Connector.SqlServer
```

Add your job runner too — for the reference implementation, the `Moberg.Warp.*` packages (see [Background jobs](/integrate/background-jobs/)).

## Dependency direction

```text
Moberg.Beacon.Api            → Core
Moberg.Beacon.AI             → Core
Moberg.Beacon.MCP            → Core, AI
Moberg.Beacon.Core.*         → Core
Moberg.Beacon.Connector.*    → Core
Moberg.Beacon.UI             → (nothing; static web assets)
```

`Moberg.Beacon.Core` itself references `Microsoft.EntityFrameworkCore.SqlServer`, so every host gets the SQL Server EF provider. Npgsql and pgvector come only with `Moberg.Beacon.Core.PostgreSql` or the PostgreSQL connector.

## Versions

- There is no version in the source code. The git release tag is the package version: publishing a GitHub release packs every packable project with that tag and pushes it to nuget.org. Four-part tags are normalised by NuGet (a tag `4.5.0.0` publishes `4.5.0`).
- Keep every `Moberg.Beacon.*` package on the **same version**. They are built and released together.
- These docs track the `main` branch. Check the [release notes](https://github.com/moberghr/beacon/releases) of the version you install for what it includes.

If you use central package management, pin one version for all of them:

```xml
<!-- Directory.Packages.props -->
<ItemGroup>
  <PackageVersion Include="Moberg.Beacon.Core" Version="$(BeaconVersion)" />
  <PackageVersion Include="Moberg.Beacon.Core.PostgreSql" Version="$(BeaconVersion)" />
  <PackageVersion Include="Moberg.Beacon.Api" Version="$(BeaconVersion)" />
  <PackageVersion Include="Moberg.Beacon.UI" Version="$(BeaconVersion)" />
  <!-- … -->
</ItemGroup>
```

## Build-time notes

- **`Microsoft.AspNetCore.OpenApi`.** If your host calls `AddOpenApi()`/`MapOpenApi()`, reference `Microsoft.AspNetCore.OpenApi` directly; relying on the copy that comes with `Moberg.Beacon.Api` fails the build with `CS9137` (interceptors).
- **Transitive package advisories.** If restore reports `NU1902`/`NU1903` for a transitive dependency and you treat warnings as errors, add a direct `PackageReference` to the patched version of that package in your project.
- **MySQL connector and `NU1608`.** `Moberg.Beacon.Connector.MySql` carries the Pomelo EF Core provider, which declares an EF Core 9 dependency range. Restore prints `NU1608`; under `TreatWarningsAsErrors`, suppress it in the project that references the connector: `<NoWarn>$(NoWarn);NU1608</NoWarn>`.
- **The UI package** contains the production React build. You do not need Node.js to consume it. The host must call `app.UseStaticFiles()` so the assets are served.
- **Building from source** (project references instead of packages): `Beacon.UI` builds the React app only in `Release`, or in `Debug` with `-p:BuildReactInDebug=true`. Node.js 22 and npm 11 match the CI build.

## Licence

Beacon is dual-licensed: GNU AGPL v3.0, or a commercial licence without copyleft obligations. Moberg partners use it under a licence exception. Embedding Beacon in a network service under the AGPL triggers its source-disclosure terms (§13). See [LICENSING.md](https://github.com/moberghr/beacon/blob/main/LICENSING.md).
