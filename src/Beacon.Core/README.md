# Beacon.Core

Core library providing semantic alerts and notifications for databases.

## Overview

Beacon.Core contains the domain models, services, and data access abstractions for monitoring databases and sending notifications via Email, Microsoft Teams, and Jira.

## Features

- Multi-step query execution with result chaining
- Cross-database support via 9 connectors (PostgreSQL, SQL Server, MySQL, BigQuery, Snowflake, Databricks, Azure Synapse, CloudWatch, generic API)
- Notification delivery via Email, Teams, and Jira
- Data migration and ETL capabilities
- Database metadata introspection
- AES-256 encrypted connection string storage (requires `Beacon:EncryptionKey`)

## Installation

```bash
# For PostgreSQL
dotnet add package Beacon.Core.PostgreSql

# For SQL Server
dotnet add package Beacon.Core.SqlServer
```

**Note**: This package is typically installed as a dependency when you install a database provider package.

## Quick Start

```csharp
using Beacon.Core;

// Configure in Program.cs (single method call)
builder.Services.AddBeacon(builder.Configuration, options =>
{
    options.UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
    // Or use SQL Server:
    // options.UseSqlServer(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

    options.AddBeaconScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com";
});
```

The React SPA (shipped as the `Beacon.UI` Razor Class Library) is served at the root URL `/`. REST endpoints
are exposed under `/beacon/api/*`, the MCP server at `/beacon/mcp`, and — in the sample host, which uses
[Moberg Warp](https://moberghr.github.io/warp/) as its `IBeaconScheduler` implementation — the background-job
dashboard at `/warp`.

## Documentation

- [Full Documentation](https://moberghr.github.io/beacon)
- [Getting Started Guide](https://moberghr.github.io/beacon)

## License

GNU AGPL v3.0 or Commercial license - see [LICENSE](https://github.com/moberghr/beacon/blob/main/LICENSE)
