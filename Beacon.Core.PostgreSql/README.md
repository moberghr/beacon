# Beacon.Core.PostgreSql

PostgreSQL database provider for Beacon.

## Overview

This package provides Entity Framework Core configuration and migrations for using PostgreSQL as the Beacon metadata database. It includes snake_case naming conventions and full support for PostgreSQL-specific features.

## Installation

```bash
dotnet add package Beacon.Core.PostgreSql
dotnet add package Beacon.UI
```

## Quick Start

### 1. Configure in Program.cs

```csharp
using Beacon.Core;
using Beacon.UI;

// Single method call for all Beacon configuration
builder.Services.AddBeacon(builder.Configuration, options =>
{
    options.UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
    options.AddBeaconScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com";
});

var app = builder.Build();

app.UseStaticFiles(); // Required for Beacon UI assets

// Serve the React SPA (Vite + TypeScript + Tailwind) at the root URL "/".
// Authentication is cookie-based with a pluggable IBeaconAuthenticationProvider
// (login form, OIDC/SSO, JWT for MCP, SHA256-hashed API keys).
app.UseBeaconUI();
app.MapBeaconApi();
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

- [Full Documentation](https://github.com/MiBu/semantico)
- [Installation Guide](https://github.com/MiBu/semantico/wiki)

## License

GNU AGPL v3.0 or Commercial license - see [LICENSE](https://github.com/MiBu/semantico/blob/main/LICENSE)
