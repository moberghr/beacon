# Semantico.Core.SqlServer

SQL Server database provider for Semantico.

## Overview

This package provides Entity Framework Core configuration and migrations for using SQL Server as the Semantico metadata database. It supports SQL Server 2019+ and Azure SQL Database.

## Installation

```bash
dotnet add package Semantico.Core.SqlServer
dotnet add package Semantico.UI.AspNet
```

## Quick Start

### 1. Configure in Program.cs

```csharp
using Semantico.Core.SqlServer;
using Semantico.UI.AspNet;

builder.Services.AddSqlServerSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico");

builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com/semantico";
});

app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/semantico");

// Run migrations
ServiceConfiguration.UseSemantico(app.Services);
```

### 2. Add Connection String

In `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Server=localhost;Database=semantico;User Id=sa;Password=YourPassword123!;TrustServerCertificate=True"
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
CREATE DATABASE semantico;
GO
USE semantico;
GO
CREATE SCHEMA semantico;
GO
```

## Documentation

- [Full Documentation](https://moberghr.github.io/semantico)
- [Installation Guide](https://moberghr.github.io/semantico/getting-started/installation)
- [Configuration Guide](https://moberghr.github.io/semantico/getting-started/configuration)

## License

MIT License - see [LICENSE](https://github.com/moberghr/semantico/blob/main/LICENSE)
