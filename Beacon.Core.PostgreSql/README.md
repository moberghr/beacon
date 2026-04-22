# Beacon.Core.PostgreSql

PostgreSQL database provider for Beacon.

## Overview

This package provides Entity Framework Core configuration and migrations for using PostgreSQL as the Beacon metadata database. It includes snake_case naming conventions and full support for PostgreSQL-specific features.

## Installation

```bash
dotnet add package Beacon.Core.PostgreSql
dotnet add package Beacon.UI.AspNet
```

## Quick Start

### 1. Configure in Program.cs

```csharp
using Beacon.Core;
using Beacon.UI.AspNet;

// Single method call for all Beacon configuration
builder.Services.AddBeacon(builder.Configuration, options =>
{
    options.UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
    options.AddBeaconScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com/beacon";
});

var app = builder.Build();

app.UseStaticFiles(); // Required for Beacon UI assets

app.UseBeaconUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/beacon");
```

### 2. Add Connection String

In `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=postgres;Password=yourpassword"
  }
}
```

## Features

- PostgreSQL 12+ support
- Entity Framework Core integration
- Automatic schema creation and migrations
- Snake_case naming conventions
- Multi-tenant support via schema isolation

## Database Setup

```sql
CREATE DATABASE beacon;
CREATE SCHEMA beacon;
GRANT ALL ON SCHEMA beacon TO postgres;
```

## Documentation

- [Full Documentation](https://moberghr.github.io/beacon)
- [Installation Guide](https://moberghr.github.io/beacon/getting-started/installation)
- [Configuration Guide](https://moberghr.github.io/beacon/getting-started/configuration)

## License

MIT License - see [LICENSE](https://github.com/moberghr/beacon/blob/main/LICENSE)
