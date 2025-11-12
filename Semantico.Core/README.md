# Semantico.Core

Core library providing semantic alerts and notifications for databases.

## Overview

Semantico.Core contains the domain models, services, and data access abstractions for monitoring databases and sending notifications via Email, Microsoft Teams, and Jira.

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
dotnet add package Semantico.Core.PostgreSql

# For SQL Server
dotnet add package Semantico.Core.SqlServer
```

**Note**: This package is typically installed as a dependency when you install a database provider package.

## Quick Start

```csharp
using Semantico.Core.PostgreSql;

// Configure in Program.cs
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico");

builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com/semantico";
});
```

## Documentation

- [Full Documentation](https://moberghr.github.io/semantico)
- [Getting Started Guide](https://moberghr.github.io/semantico/getting-started/quick-start)
- [Configuration Guide](https://moberghr.github.io/semantico/getting-started/configuration)

## License

MIT License - see [LICENSE](https://github.com/moberghr/semantico/blob/main/LICENSE)
