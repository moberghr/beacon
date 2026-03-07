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
using Semantico.Core;

// Configure in Program.cs (single method call)
builder.Services.AddSemantico(builder.Configuration, options =>
{
    options.UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");
    // Or use SQL Server:
    // options.UseSqlServer(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");

    options.AddSemanticoScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com/semantico";
});
```

## Documentation

- [Full Documentation](https://mibu.github.io/semantico)
- [Getting Started Guide](https://mibu.github.io/semantico/getting-started/quick-start)
- [Configuration Guide](https://mibu.github.io/semantico/getting-started/configuration)

## License

MIT License - see [LICENSE](https://github.com/MiBu/semantico/blob/main/LICENSE)
