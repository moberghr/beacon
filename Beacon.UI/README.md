# Beacon.UI

Blazor UI components for Beacon admin interface.

## Overview

This package contains the Blazor components and MudBlazor-based admin interface for managing Beacon data sources, queries, subscriptions, and notifications.

## Installation

```bash
dotnet add package Beacon.Core.PostgreSql
dotnet add package Beacon.UI.AspNet
```

**Note**: This package is typically installed as a dependency when you install `Beacon.UI.AspNet`.

## Features

- Modern Blazor Server UI with MudBlazor components
- SQL editor with syntax highlighting and autocomplete
- Database explorer with schema introspection
- Query execution preview and testing
- Subscription management with cron scheduling
- Notification history and analytics
- Data migration job monitoring
- Real-time execution metrics

## Components Included

- **Data Sources**: Manage database connections
- **Queries**: Create and test multi-step queries
- **Subscriptions**: Schedule automated query execution
- **Recipients**: Configure notification delivery channels
- **Notifications**: View delivery history and results
- **Data Migration**: Monitor ETL job execution

## Usage

This package is designed to be hosted in an ASP.NET Core application using the `Beacon.UI.AspNet` package:

```csharp
using Beacon.UI.AspNet;

app.UseBeaconUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/beacon");
```

Access the UI at: `http://localhost:5000/beacon`

## Documentation

- [Full Documentation](https://moberghr.github.io/beacon)
- [Quick Start Guide](https://moberghr.github.io/beacon/getting-started/quick-start)
- [Features Overview](https://moberghr.github.io/beacon/features/)

## License

MIT License - see [LICENSE](https://github.com/moberghr/beacon/blob/main/LICENSE)
