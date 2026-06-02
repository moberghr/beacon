# Beacon Knowledge Base Index

## Overview

Beacon is a .NET library for semantic database monitoring, alerting, and data orchestration. It enables scheduled SQL query execution with multi-channel notifications (Teams, Slack, Email, Jira) and ETL capabilities across PostgreSQL, SQL Server, and MySQL databases.

---

## Architecture Summary

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            BEACON ARCHITECTURE                            │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│   ┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐  │
│   │   Blazor    │    │   MudBlazor │    │  Highlight  │    │   Mermaid   │  │
│   │   Server    │    │     8.0     │    │    .js      │    │     .js     │  │
│   └──────┬──────┘    └──────┬──────┘    └──────┬──────┘    └──────┬──────┘  │
│          │                  │                  │                  │         │
│          └──────────────────┴──────────────────┴──────────────────┘         │
│                                    │                                        │
│                         ┌──────────▼──────────┐                             │
│                         │    Beacon.UI     │ ◄─── UI Components          │
│                         └──────────┬──────────┘                             │
│                                    │                                        │
│          ┌─────────────────────────┼─────────────────────────┐              │
│          │                         │                         │              │
│   ┌──────▼──────┐          ┌───────▼───────┐         ┌───────▼───────┐      │
│   │   Query     │          │ Subscription  │         │  Notification │      │
│   │  Service    │          │   Service     │         │    Service    │      │
│   └──────┬──────┘          └───────┬───────┘         └───────┬───────┘      │
│          │                         │                         │              │
│          └─────────────────────────┼─────────────────────────┘              │
│                                    │                                        │
│                         ┌──────────▼──────────┐                             │
│                         │   Adapter Factory   │                             │
│                         └──────────┬──────────┘                             │
│                                    │                                        │
│     ┌─────────┬─────────┬──────────┴──────────┬─────────┐                   │
│     │         │         │                     │         │                   │
│  ┌──▼──┐  ┌───▼───┐  ┌──▼──┐             ┌────▼────┐ ┌──▼───┐               │
│  │Teams│  │ Slack │  │Email│             │  Jira   │ │ Task │               │
│  └─────┘  └───────┘  └─────┘             └─────────┘ └──────┘               │
│                                                                             │
│          ┌───────────────────────────────────────────────────┐              │
│          │              BeaconContext (EF Core)           │              │
│          └───────────────────────────┬───────────────────────┘              │
│                                      │                                      │
│               ┌──────────────────────┼──────────────────────┐               │
│               │                      │                      │               │
│          ┌────▼────┐           ┌─────▼─────┐          ┌─────▼─────┐         │
│          │PostgreSQL│          │ SQL Server│          │   MySQL   │         │
│          └─────────┘           └───────────┘          └───────────┘         │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Knowledge Base Documents

| Document | Description |
|----------|-------------|
| [01-database-schema.md](./01-database-schema.md) | Entity models, relationships, enums, ERD |
| [02-services-architecture.md](./02-services-architecture.md) | Service interfaces, methods, business logic |
| [03-notification-adapters.md](./03-notification-adapters.md) | Teams, Slack, Email, Jira adapters |
| [04-ui-components.md](./04-ui-components.md) | Blazor pages, custom components, patterns |
| [05-configuration.md](./05-configuration.md) | Setup, DI, appsettings, authentication |

---

## Core Concepts

### Multi-Step Queries
Queries can have multiple steps, each targeting different data sources. Results are stored in virtual tables (@result1, @result2, etc.) and combined via SQLite for cross-database joins.

### Subscriptions
Scheduled execution of queries using cron expressions. Each subscription can notify multiple recipients via different channels.

### Tasks
Auto-created from subscriptions when `CreateTasks` is enabled. Tasks track issue lifecycle with auto-resolution when query returns 0 results.

### Adapters
Notification delivery plugins implementing `IAdapter`. Factory pattern routes based on `NotificationType` enum.

---

## Key Entities

| Entity | Purpose |
|--------|---------|
| `Query` | SQL query definition with multi-step support |
| `QueryStep` | Individual step in a query, targets specific data source |
| `Subscription` | Scheduled execution configuration |
| `Recipient` | Notification destination (email, webhook, etc.) |
| `QueryTask` | Issue tracking with lifecycle management |
| `DataSource` | Database connection configuration |
| `MigrationJob` | ETL job definition |

---

## Key Services

| Service | Responsibility |
|---------|----------------|
| `QueryService` | Query CRUD, execution, step management |
| `SubscriptionService` | Subscription lifecycle, scheduler integration |
| `NotificationService` | Dispatch to adapters, history tracking |
| `TaskService` | Task lifecycle, auto-resolution, comments |
| `DataSourceService` | Connection management, metadata refresh |
| `MigrationService` | ETL execution, history tracking |

---

## Notification Adapters

| Adapter | Max Cols | Max Rows | Format |
|---------|----------|----------|--------|
| **Slack** | 20 | 100 | Block Kit Tables |
| **Teams** | 3 | 10 | Adaptive Cards |
| **Email** | Unlimited | 10 (inline) | HTML + Attachment |
| **Jira** | Unlimited | 10 | Markdown + Attachment |

---

## Quick Reference

### Database Operations
```csharp
// Create migration
dotnet ef migrations add Name --project Beacon.Core.PostgreSql

// Apply migration
dotnet ef database update --project Beacon.Core.PostgreSql
```

### Service Registration
```csharp
// PostgreSQL
builder.Services.AddPostgreSqlBeacon(connectionString, "schema");

// Configure services
builder.Services.AddBeaconAdmin(config, options => {
    options.AddBeaconScheduler<MyScheduler>();
    options.BaseUrl = "https://example.com/beacon";
});

// UI
app.UseBeaconUI()
   .UseBasicAuthentication("admin", "pass")
   .AddBlazorUI("/beacon");
```

### Query Execution Flow
```
Scheduler → JobService.ExecuteQuery()
    → QueryService.ExecuteQuery()
        → ExecuteQuerySteps() [per step]
        → VirtualTableManager [combine results]
        → ExecuteFinalQuery() [SQLite]
    → NotificationService.SendNotification()
        → AdapterFactory.GetAdapter()
        → IAdapter.SendNotificationAsync()
    → TaskService.CreateOrUpdateTask() [if enabled]
```

---

## Technology Stack

| Category | Technologies |
|----------|-------------|
| **Runtime** | .NET 9.0, C# 13 |
| **UI** | Blazor Server, MudBlazor 8.0 |
| **ORM** | Entity Framework Core 9.0, Dapper |
| **Databases** | PostgreSQL, SQL Server, MySQL, SQLite (virtual tables) |
| **Scheduling** | IBeaconScheduler (Hangfire/Quartz.NET) |
| **Integrations** | Atlassian.SDK, AdaptiveCards, ClosedXML |

---

## File Locations

```
Beacon.Core/
├── Data/Entities/                    # Entity classes
├── Data/Enums/                       # NotificationType, MigrationMode, etc.
├── Services/                         # Business logic
├── Adapters/                         # Notification adapters
│   ├── Slack/SlackAdapter.cs
│   ├── Teams/TeamsAdapter.cs
│   ├── Mail/EmailAdapter.cs
│   └── Jira/JiraAdapter.cs
└── ServiceConfiguration.cs           # DI setup

Beacon.UI/Components/
├── Layout/MainLayout.razor           # App layout
├── Pages/                            # Feature pages
└── Custom/                           # Reusable components
```

---

## Common Patterns

### Soft Delete
Entities inherit `ArchivableBaseEntity` with `ArchivedTime` property. EF Core global query filters exclude archived records.

### Factory Pattern
`AdapterFactory` routes notifications by `NotificationType` to appropriate adapter implementations.

### Virtual Tables
`VirtualTableManager` creates SQLite in-memory tables for cross-database query results, enabling JOINs across different database engines.

### Form Validation
MudBlazor forms with `OnlyValidateIfDirty` for user-friendly validation.

---

## Development Tips

1. **Don't use .Include()** when followed by `.Select(new ...)` - EF Core generates JOINs automatically

2. **Use WhereIf()** for conditional filtering:
   ```csharp
   query.WhereIf(id.HasValue, x => x.Id == id)
   ```

3. **Masked destinations** - Sensitive data shown as `••••••••` in UI, preserved if unchanged

4. **Auto-resolution** - Tasks auto-resolve when query returns 0 results

5. **Schema-agnostic** - Migrations work with any schema name specified at runtime
