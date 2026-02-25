# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Semantico** is a .NET 9.0 library for semantic database monitoring, alerting, data orchestration, and AI-powered documentation. It enables scheduled SQL query execution with multi-channel notifications (Email, Teams, Slack, Jira, Webhooks) and ETL capabilities across PostgreSQL, SQL Server, and MySQL databases.

**Version:** 2.0.5.0 | **License:** MIT | **Framework:** .NET 9.0 / C# 13

## Build Commands

- **Build solution:** `dotnet build --property WarningLevel=0`
- **Run application:** `dotnet run --project Semantico.SampleProject`
- **Watch for changes:** `dotnet watch run --project Semantico.SampleProject`
- **Run tests:** `dotnet test`
- **Restore packages:** `dotnet restore`

## Solution Structure

```
Semantico.sln
├── Semantico.Core/              # Core domain logic, services, entities, adapters
├── Semantico.Core.PostgreSql/   # PostgreSQL EF Core provider + migrations
├── Semantico.Core.SqlServer/    # SQL Server EF Core provider + migrations
├── Semantico.AI/                # AI/LLM extensions (optional add-on)
├── Semantico.UI/                # Blazor Server UI (Razor Class Library)
├── Semantico.SampleProject/     # Sample ASP.NET Core host application
└── Semantico.Tests/             # Unit tests (NUnit + Moq + FluentAssertions + bUnit)
```

### Project Dependencies

```
Semantico.SampleProject (ASP.NET Web App)
  ├── Semantico.Core
  ├── Semantico.Core.PostgreSql → Semantico.Core
  ├── Semantico.Core.SqlServer  → Semantico.Core
  ├── Semantico.AI              → Semantico.Core
  └── Semantico.UI              → Semantico.Core
```

## Key Technologies

| Category | Technologies |
|----------|-------------|
| **Runtime** | .NET 9.0, C# 13, ASP.NET Core 9.0 |
| **UI** | Blazor Server, MudBlazor 8.15, BlazorMonaco (SQL editor) |
| **ORM** | Entity Framework Core 9.0, Dapper |
| **Databases** | PostgreSQL (Npgsql), SQL Server, MySQL, SQLite (virtual tables) |
| **AI/LLM** | OpenAI, Anthropic Claude, Azure OpenAI, AWS Bedrock |
| **CQRS** | MediatR 12.4 |
| **Testing** | NUnit 4.2, Moq, FluentAssertions, bUnit, EF Core InMemory |
| **Notifications** | AdaptiveCards (Teams), ClosedXML (Excel), CsvHelper, Atlassian.SDK (Jira) |
| **Scheduling** | ISemanticoScheduler interface (consumer implements with Hangfire/Quartz.NET) |
| **Docs Gen** | QuestPDF (PDF), Markdig (Markdown), Mermaid.js (diagrams) |

## Architecture

Semantico follows **Clean Architecture** with clear layer separation:

- **Semantico.Core** — Domain entities, services, adapters, handlers (business logic)
- **Semantico.Core.PostgreSql / SqlServer** — Database provider implementations + EF migrations
- **Semantico.AI** — Optional AI extensions (LLM providers, documentation, alert generation)
- **Semantico.UI** — Blazor presentation layer (pages, components, middleware)

### Core Project Layout

```
Semantico.Core/
├── Abstractions/                 # Shared interfaces
├── Adapters/                     # Notification adapters
│   ├── Jira/                     #   JiraAdapter
│   ├── Mail/                     #   IEmailAdapter (consumer implements)
│   ├── Shared/                   #   AdapterFactory, IAdapter
│   ├── Slack/                    #   SlackAdapter (Block Kit)
│   ├── Teams/                    #   TeamsAdapter (Adaptive Cards)
│   └── Webhook/                  #   WebhookAdapter
├── Authentication/               # Auth providers
│   └── Providers/                #   DatabaseAuthenticationProvider
├── Authorization/                # RBAC authorization
│   └── Providers/                #   DatabaseAuthorizationProvider
├── Configuration/                # SemanticoConfiguration options
├── Data/
│   ├── Entities/                 # EF Core entity classes
│   │   ├── Base/                 #   BaseEntity, ArchivableBaseEntity
│   │   ├── DataMigration/        #   MigrationJob, MigrationExecutionHistory
│   │   ├── DataQuality/          #   Data quality entities
│   │   └── Metadata/             #   DatabaseMetadata, ColumnMetadata, IndexMetadata
│   ├── Enums/                    #   NotificationType, MigrationMode, DatabaseEngineType, etc.
│   └── SemanticoContext.cs       # Base DbContext
├── DTOs/                         # Data transfer objects
├── Exceptions/                   # SemanticoException
├── Handlers/                     # MediatR request/command handlers
│   ├── Ai/                       #   AI-related handlers
│   ├── Approvals/                #   Approval workflow handlers
│   ├── Dashboards/               #   Dashboard CRUD handlers
│   ├── DataQuality/              #   Data quality handlers
│   ├── DataSources/              #   DataSource handlers
│   ├── Queries/                  #   Query CRUD handlers
│   ├── QueryFolders/             #   Query folder handlers
│   └── QueryVersions/            #   Version management handlers
├── Helpers/                      # QueryHelper, ParameterEntityFactory, BulkHelpers, PagedList
├── Models/                       # DTOs organized by feature
├── Services/                     # Business logic services
│   ├── Providers/                #   CloudWatch provider
│   ├── Security/                 #   Encryption, JWT services
│   └── Shared/                   #   VirtualTableManager
├── Validators/                   # QueryValidator, SubscriptionValidator
└── Worker/                       # Job execution (ISemanticoScheduler, JobService)
```

### AI Project Layout

```
Semantico.AI/
├── Handlers/Ai/                  # MediatR handlers for AI operations
├── Models/
│   ├── Ai/                       # AI request/response models
│   ├── Configuration/            # LlmConfiguration, AiProvider enum
│   └── MultiAgent/               # Multi-agent coordination models
├── Services/
│   ├── Ai/                       # Documentation, Alert, AI Actor services
│   │   ├── AiActor/              #   Autonomous database monitoring agents
│   │   ├── DocumentationAgent/   #   Multi-phase documentation generation
│   │   └── MultiAgent/           #   Parallel domain-specific agents
│   └── LlmProviders/             # OpenAI, Claude, Azure, Bedrock providers
└── ServiceConfiguration.cs       # DI registration (AddSemanticoAI)
```

### UI Project Layout

```
Semantico.UI/
├── Authentication/               # Auth middleware (Basic, Cookie, JWT, LoginForm)
├── Components/
│   ├── Custom/                   # Reusable components (SqlEditor, DatabaseExplorer, etc.)
│   ├── Helpers/                  # Component helper utilities
│   ├── Layout/                   # MainLayout, LoginLayout, SetupLayout
│   ├── Pages/                    # Feature pages organized by domain
│   │   ├── AiActors/            Approvals/        ControlTower/
│   │   ├── Dashboards/          DataMigration/    DataQuality/
│   │   ├── DataSources/         Notifications/    Queries/
│   │   ├── QueryExecutionHistory/  Recipients/    Subscriptions/
│   │   ├── Tasks/               Users/
│   │   └── Home.razor, Login.razor, Settings.razor, AdminSettings.razor
│   └── Shared/                   # BasePageComponent, SemanticoPageHeader, etc.
├── Helpers/                      # Utility helpers
├── wwwroot/                      # Static assets (CSS, JS, highlight.js, Mermaid)
└── ServiceExtensions.cs          # AddSemanticoUI(), AddSemanticoCookieAuthentication()
```

## Key Services

| Service | Interface | Responsibility |
|---------|-----------|----------------|
| QueryService | IQueryService | Query CRUD, multi-step execution, cross-DB orchestration |
| SubscriptionService | ISubscriptionService | Scheduled execution, cron, recipient management |
| NotificationService | INotificationService | Dispatch to adapters, execution history |
| TaskService | ITaskService | Alert lifecycle, auto-resolution, comments |
| DataSourceService | IDataSourceService | Connection management, metadata refresh |
| MigrationService | IMigrationService | ETL execution (Insert/Upsert/Truncate/Sync) |
| RecipientService | IRecipientService | Notification recipient CRUD |
| DatabaseMetadataService | IDatabaseMetadataService | Schema introspection and caching |
| CommentService | ICommentService | Generic entity comments |
| JobService | IJobService | Scheduled job execution orchestration |
| StatisticsService | IStatisticsService | Dashboard statistics aggregation |

**All services are registered as Transient** with interface + internal implementation pattern.

## Core Entities

| Entity | Base Class | Purpose |
|--------|-----------|---------|
| DataSource | ArchivableBaseEntity | Database connection (encrypted connection strings) |
| Query | ArchivableBaseEntity | SQL query definition with multi-step support |
| QueryStep | BaseEntity | Individual step targeting a specific data source |
| Subscription | ArchivableBaseEntity | Cron-scheduled query execution configuration |
| Recipient | ArchivableBaseEntity | Notification destination (email, webhook URL, etc.) |
| QueryTask | ArchivableBaseEntity | Alerting task with auto-resolution lifecycle |
| QueryExecutionHistory | BaseEntity | Execution audit trail with timing metrics |
| Notification | BaseEntity | Delivery record per recipient per execution |
| MigrationJob | ArchivableBaseEntity | ETL job definition |
| Comment | BaseEntity | Generic entity comment (EntityType + EntityId pattern) |
| DatabaseMetadata | ArchivableBaseEntity | Cached schema metadata |

### Soft Delete Pattern

All main entities use `ArchivableBaseEntity` with an `ArchivedTime` property. EF Core global query filters automatically exclude archived records (`WHERE ArchivedTime IS NULL`). Use `.Archive()` to soft-delete and `.Unarchive()` to restore.

## Database Migrations

Migrations are **provider-specific** and **schema-agnostic** (schema is applied at runtime).

### Generate a Migration

```bash
# For PostgreSQL
dotnet ef migrations add MigrationName \
  --project Semantico.Core.PostgreSql \
  --startup-project Semantico.SampleProject

# For SQL Server
dotnet ef migrations add MigrationName \
  --project Semantico.Core.SqlServer \
  --startup-project Semantico.SampleProject
```

### Apply Migrations

```bash
dotnet ef database update \
  --project Semantico.Core.PostgreSql \
  --startup-project Semantico.SampleProject
```

Migrations are also **automatically applied** at startup via `ServiceConfiguration.UseSemantico()`, which creates the schema if needed and runs pending migrations.

### Important Migration Rules

- **PostgreSQL migrations** go in `Semantico.Core.PostgreSql/Data/Migrations/`
- **SQL Server migrations** go in `Semantico.Core.SqlServer/Data/Migrations/`
- Migrations must NOT contain hardcoded schema names — schema is set at runtime via `HasDefaultSchema()`
- PostgreSQL uses **snake_case** column naming (via EFCore.NamingConventions)
- Entity changes go in `Semantico.Core/Data/Entities/`, then generate migrations for both providers

## Coding Conventions

### Naming

- **Classes/Types:** PascalCase (`ProjectService`, `QueryParameter`)
- **Interfaces:** `I` prefix + PascalCase (`IAdapter`, `IProjectService`)
- **Methods:** PascalCase (`CreateProject`, `DeleteQuery`)
- **Properties:** PascalCase (`Name`, `ConnectionString`)
- **Private fields:** `_` prefix + camelCase (`_context`, `_dataGrid`)
- **Local variables:** camelCase (`project`, `queryParameter`)
- **Parameters:** camelCase (`queryData`, `cancellationToken`)

### Formatting

- **Indentation:** 4 spaces
- **Braces:** Allman style (opening brace on own line)
- **LINQ:** Chain calls indented on new lines
- **Namespaces:** File-scoped (`namespace Semantico.Core;`)

### Patterns

- **Services:** Public interface + `internal class` implementation
- **Async:** All service methods are async with `CancellationToken` support
- **DI:** Constructor injection via primary constructors or traditional constructors
- **Database access:** `IDbContextFactory<SemanticoContext>` (not direct DbContext injection)
- **CQRS:** MediatR handlers for feature-specific operations
- **Errors:** `SemanticoException` for domain errors, `BaseResponse` for service results
- **Validation:** Validator classes with early validation before processing
- **Conditional queries:** `WhereIf()` extension for optional filtering
- **Pagination:** `ToPagedListAsync()` extension
- **Nullable:** Enabled project-wide, use `required` modifier for non-nullable properties

### Blazor/UI Patterns

- All pages inherit `BasePageComponent` (`@inherits BasePageComponent`)
- MudBlazor components for all UI elements (MudDataGrid, MudDialog, etc.)
- Feature pages organized in domain folders under `Components/Pages/`
- `SemanticoPageHeader`, `SemanticoPageAlert`, `SemanticoPageTitle` shared components
- Responsive dialogs via `CreateResponsiveDialogOptions()` helper
- Dark mode support with local storage persistence

### Common Mistakes to Avoid

- Do NOT use `.Include()` when followed by `.Select(new ...)` — EF Core generates JOINs automatically in projections
- Do NOT hardcode schema names in migrations — they are applied at runtime
- Do NOT inject `SemanticoContext` directly — use `IDbContextFactory<SemanticoContext>`
- Do NOT store connection strings unencrypted — use `IEncryptionService`

## Service Registration (How It All Fits Together)

```csharp
// 1. Core services + database provider
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();  // Required
        options.BaseUrl = "https://your-domain.com/semantico";
        options.UseAI = true;  // Enable AI features
        options.AddEmailAdapter<YourEmailAdapter>();  // Optional
        options.Authorization.Enabled = true;  // Optional RBAC
    })
    .UsePostgreSql(connectionString, "semantico");  // or .UseSqlServer(...)

// 2. UI components
builder.Services.AddSemanticoUI();

// 3. AI services (optional)
builder.Services.AddSemanticoAI(builder.Configuration);

// Middleware pipeline
app.UseStaticFiles();  // Required before UseSemanticoUI
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")  // or .UseLoginForm()
    .UseAuthorization()  // Optional RBAC
    .AddBlazorUI("/semantico");
```

## Configuration (appsettings.json)

```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=localhost;Database=semantico;Username=postgres;Password=yourpassword"
  },
  "Semantico": {
    "EncryptionKey": "your-secure-32-character-key-here",
    "LLM": {
      "Provider": "Claude",
      "ApiKey": "your-api-key",
      "Model": "claude-sonnet-4-20250514",
      "FastModel": "claude-haiku-4-20250514",
      "Limits": {
        "MaxConcurrentRequests": 50,
        "RequestsPerMinute": 1000,
        "MonthlyBudget": 100.00
      }
    }
  }
}
```

**Supported LLM Providers:** OpenAI, Claude/Anthropic, Azure OpenAI, AWS Bedrock

## Testing

- **Framework:** NUnit 4.2 with `Microsoft.NET.Test.Sdk`
- **Mocking:** Moq for service dependencies
- **Assertions:** FluentAssertions
- **UI Testing:** bUnit for Blazor component tests
- **Database:** EF Core InMemory provider for service tests
- **Run tests:** `dotnet test` or `dotnet test --configuration Release`

## CI/CD

The repository uses GitHub Actions (`.github/workflows/w-build.yml`):
- **Trigger:** On release publish or manual workflow dispatch
- **Steps:** Checkout → Setup .NET 9.0 → Restore → Build → Test → Pack & Publish to NuGet
- **Packable projects:** Semantico.Core, Semantico.Core.PostgreSql, Semantico.Core.SqlServer, Semantico.AI, Semantico.UI

## Notification Adapters

| Adapter | Max Cols | Max Rows (inline) | Attachment | Format |
|---------|----------|-------------------|------------|--------|
| Email | Unlimited | 10 | CSV/Excel | HTML tables |
| Slack | 20 | 100 | No | Block Kit Tables |
| Teams | 3 | 10 | No | Adaptive Cards |
| Jira | Unlimited | 50 | CSV | Markdown |
| Webhook | N/A | N/A | JSON payload | Custom template |

All adapters implement `IAdapter` interface and are routed via `AdapterFactory` based on `NotificationType` enum.

## Query Execution Flow

```
Scheduler (Cron trigger)
  → JobService.ExecuteQuery(subscriptionId)
    → QueryService.ExecuteQueryAdvanced()
      → For each QueryStep (in order):
          → Execute SQL against step's DataSource (via Dapper)
          → Store result in VirtualTableManager (@@resultN)
      → If FinalQuery exists:
          → Execute against SQLite virtual tables (cross-DB joins)
    → NotificationService.SendNotification()
      → AdapterFactory.GetAdapter(notificationType)
      → IAdapter.SendNotificationAsync()
    → TaskService.CreateOrUpdateTask() (if CreateTasks enabled)
      → Auto-resolve if result count == 0
```

## Useful Reference Files

| File | Purpose |
|------|---------|
| `Semantico.SampleProject/Program.cs` | Complete setup example with all features |
| `Semantico.Core/ServiceConfiguration.cs` | Core DI registration |
| `Semantico.AI/ServiceConfiguration.cs` | AI DI registration |
| `Semantico.UI/ServiceExtensions.cs` | UI DI registration + middleware |
| `Semantico.Core/Data/SemanticoContext.cs` | Base DbContext with all DbSets |
| `Semantico.Core/Worker/ISemanticoScheduler.cs` | Scheduler interface contract |
| `CLAUDE_STYLE_GUIDE.md` | Detailed coding style guide |
| `MIGRATIONS.md` | Migration creation instructions |
| `SCHEMA_AGNOSTIC_MIGRATIONS.md` | Multi-tenant schema migration guide |
| `.claude/knowledge-base/` | Detailed architecture documentation |
