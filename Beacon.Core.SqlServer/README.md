# Beacon.Core.SqlServer

SQL Server database provider for Beacon.

## Overview

This package provides Entity Framework Core configuration and migrations for using SQL Server as the Beacon metadata database. It supports SQL Server 2019+ and Azure SQL Database.

## Installation

```bash
dotnet add package Beacon.Core.SqlServer
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
    options.UseSqlServer(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
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
    "BeaconContext": "Server=localhost;Database=beacon;User Id=sa;Password=YourPassword123!;TrustServerCertificate=True"
  }
}
```

## Features

- SQL Server 2019+ support
- Azure SQL Database support
- Entity Framework Core integration
- Automatic schema creation and migrations
- Multi-tenant support via schema isolation

## Database Setup

```sql
CREATE DATABASE beacon;
GO
USE beacon;
GO
CREATE SCHEMA beacon;
GO
```

## Documentation

- [Full Documentation](https://github.com/MiBu/semantico)
- [Installation Guide](https://github.com/MiBu/semantico/wiki)

## License

MIT License - see [LICENSE](https://github.com/MiBu/semantico/blob/main/LICENSE)
