# Semantico.Core.PostgreSql

PostgreSQL database provider for Semantico.

## Overview

This package provides Entity Framework Core configuration and migrations for using PostgreSQL as the Semantico metadata database. It includes snake_case naming conventions and full support for PostgreSQL-specific features.

## Installation

```bash
dotnet add package Semantico.Core.PostgreSql
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
    options.UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");
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
    "SemanticoContext": "Host=localhost;Database=semantico;Username=postgres;Password=yourpassword"
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
CREATE DATABASE semantico;
CREATE SCHEMA semantico;
GRANT ALL ON SCHEMA semantico TO postgres;
```

## Documentation

- [Full Documentation](https://moberghr.github.io/semantico)
- [Installation Guide](https://moberghr.github.io/semantico/getting-started/installation)
- [Configuration Guide](https://moberghr.github.io/semantico/getting-started/configuration)

## License

MIT License - see [LICENSE](https://github.com/moberghr/semantico/blob/main/LICENSE)
