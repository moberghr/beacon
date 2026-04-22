# Beacon.Core

Core library providing semantic alerts and notifications for databases.

## Overview

Beacon.Core contains the domain models, services, and data access abstractions for monitoring databases and sending notifications via Email, Microsoft Teams, and Jira.

## Features

- Multi-step query execution with result chaining
- Cross-database support (PostgreSQL, SQL Server, MySQL)
- Notification delivery via Email, Teams, and Jira
- Data migration and ETL capabilities
- Database metadata introspection
- Encrypted connection string storage

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
    options.BaseUrl = "https://your-domain.com/beacon";
});
```

## Documentation

- [Full Documentation](https://moberghr.github.io/beacon)
- [Getting Started Guide](https://moberghr.github.io/beacon/getting-started/quick-start)
- [Configuration Guide](https://moberghr.github.io/beacon/getting-started/configuration)

## License

MIT License - see [LICENSE](https://github.com/moberghr/beacon/blob/main/LICENSE)
