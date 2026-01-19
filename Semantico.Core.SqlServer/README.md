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
using Semantico.Core;
using Semantico.UI.AspNet;

// Single method call for all Semantico configuration
builder.Services.AddSemantico(builder.Configuration, options =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");
    options.AddSemanticoScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com/semantico";
});

var app = builder.Build();

app.UseStaticFiles(); // Required for Semantico UI assets

app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/semantico");
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
