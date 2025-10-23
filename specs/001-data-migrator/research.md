# Data Migrator Research - Semantico Project Architecture Analysis

## Executive Summary

This document analyzes the current Semantico project architecture to understand patterns and structures that can be extended for the Data Migration feature. The analysis focuses on query execution patterns, entity relationships, UI patterns, and extension points.

## Current Query Execution Architecture

### Core Query Execution Flow

The query execution system is built around a sophisticated multi-step, cross-project architecture:

**Key Service: `QueryService` (`/Users/mirkobudimir/Dev/semantico/Semantico.Core/Services/QueryService.cs`)**

#### Multi-Step Query Support
- **Query Structure**: Queries can have multiple steps (`QueryStep`) that execute across different projects/databases
- **Cross-Project Execution**: Each `QueryStep` can target a different `Project` with different database engines (PostgreSQL, MySQL, MSSQL)
- **Result Chaining**: Steps reference previous results using `@result1`, `@result2`, etc. syntax
- **Final Query**: Optional final query executes against in-memory SQLite with all step results loaded

#### Key Methods:
```csharp
// Advanced execution with support for cross-project multi-step queries
Task<QueryExecutionResult> ExecuteQueryAdvanced(int queryId, string? finalQuery = null, List<ParameterValue>? parameters = null, CancellationToken cancellationToken = default)

// Preview individual steps
Task<QueryStepResult> PreviewQueryStep(int queryId, int stepOrder, CancellationToken cancellationToken)

// Legacy single-query execution (backward compatibility)
Task<QueryResult> ExecuteQuery(int subscriptionId, CancellationToken cancellationToken)
```

#### Virtual Table Management
- **VirtualTableManager**: Handles in-memory SQLite database for cross-database final queries
- **@result Pattern**: `@result1`, `@result2`, etc. are replaced with actual table names in final queries
- **ProjectInfo Context**: Each virtual table maintains project context (name, database engine type)

### Notification Processing

**Key Service: `NotificationService` (`/Users/mirkobudimir/Dev/semantico/Semantico.Core/Services/NotificationService.cs`)**

#### Notification Flow:
1. **Query Execution**: `JobService.ExecuteQuery()` runs scheduled queries
2. **Status Determination**: Based on results, timeout, and previous executions
3. **History Creation**: `QueryExecutionHistory` record created with status
4. **Notification Records**: Individual `Notification` records for each recipient
5. **Adapter Pattern**: Different notification types (Email, Teams, Jira) via `IAdapter`

#### Notification Statuses:
- `NotificationSent`: Results changed or first execution
- `NotificationSilenced`: Same results as previous execution  
- `NoResults`: Query returned no data
- `Timeout`: Query execution exceeded timeout

## Entity Structure and Database Schema

### Core Entity Hierarchy

**Base Classes:**
```csharp
// /Users/mirkobudimir/Dev/semantico/Semantico.Core/Data/Entities/Base/BaseEntity.cs
internal abstract class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
}

// /Users/mirkobudimir/Dev/semantico/Semantico.Core/Data/Entities/Base/ArchivableBaseEntity.cs  
internal abstract class ArchivableBaseEntity : BaseEntity
{
    public DateTime? ArchivedTime { get; set; }
    public void Archive() { ... }
    public void Unarchive() { ... }
}
```

### Key Entities for Migration Feature

#### Project Entity
```csharp
// /Users/mirkobudimir/Dev/semantico/Semantico.Core/Data/Entities/Project.cs
internal class Project : ArchivableBaseEntity
{
    public required string Name { get; set; }
    public required string ConnectionString { get; set; }
    public required DatabaseEngineType DatabaseEngineType { get; set; }
    public List<QueryStep> QuerySteps { get; set; } = new();
}
```

**Extension Point**: Projects contain database connection details and engine types - perfect foundation for migration destination configuration.

#### Query Execution System
```csharp
// /Users/mirkobudimir/Dev/semantico/Semantico.Core/Data/Entities/Query.cs
internal class Query : ArchivableBaseEntity
{
    public string Name { get; set; }
    public string? Description { get; set; }
    public string? FinalQuery { get; set; } // Uses @result1, @result2, etc.
    public List<QueryStep> Steps { get; set; } = new();
    
    // Computed properties
    public bool IsMultiStep => Steps.Count > 1;
    public bool IsCrossProject => Steps.Select(s => s.ProjectId).Distinct().Count() > 1;
    public bool IsCrossDatabase => Steps.Select(s => s.Project.DatabaseEngineType).Distinct().Count() > 1;
}

// /Users/mirkobudimir/Dev/semantico/Semantico.Core/Data/Entities/QueryStep.cs
internal class QueryStep : BaseEntity
{
    public required int QueryId { get; set; }
    public required int ProjectId { get; set; } // Each step targets different project/DB
    public required int StepOrder { get; set; }
    public required string SqlValue { get; set; }
    public string? Name { get; set; }
    public List<QueryStepParameter> Parameters { get; set; } = new();
}
```

**Extension Point**: The multi-step, cross-project query architecture is perfectly suited for migration operations where:
1. Step 1: Extract data from source database
2. Step 2: Transform/validate data
3. Step 3: Load data into destination database

#### QueryExecutionHistory and Notifications
```csharp
// /Users/mirkobudimir/Dev/semantico/Semantico.Core/Data/Entities/QueryExecutionHistory.cs
internal class QueryExecutionHistory : BaseEntity
{
    public required int SubscriptionId { get; set; }
    public required int ResultCount { get; set; }
    public required string CompiledSql { get; set; }
    public required NotificationStatus NotificationStatus { get; set; }
    public required double ExecutionTimeMs { get; set; }
    public List<Notification> Notifications { get; set; } = new();
}

// /Users/mirkobudimir/Dev/semantico/Semantico.Core/Data/Entities/Notification.cs  
internal class Notification : BaseEntity
{
    public required int RecipientId { get; set; }
    public required NotificationType Type { get; set; }
    public required DateTime SentAt { get; set; }
    public string? Results { get; set; } // JSON results storage
}
```

**Extension Point**: Existing notification and history tracking can be extended for migration job progress monitoring and alerting.

### Database Context
```csharp
// /Users/mirkobudimir/Dev/semantico/Semantico.Core/Data/SemanticoContext.cs
internal class SemanticoContext : DbContext
{
    public DbSet<Query> Queries => Set<Query>();
    public DbSet<QueryStep> QuerySteps => Set<QueryStep>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<QueryExecutionHistory> QueryExecutionHistory => Set<QueryExecutionHistory>();
    // ... other entities
}
```

**Extension Point**: New migration entities can be added to the existing context with proper soft-delete filtering already implemented.

## UI Component Patterns

### Page Component Architecture

**Base Pattern:**
```csharp
// /Users/mirkobudimir/Dev/semantico/Semantico.UI/Components/Shared/BasePageComponent.cs
public class BasePageComponent: ComponentBase
{
    [Inject] protected NavigationManager NavManager { get; set; }
    [Inject] protected PageHistoryState PageState { get; set; }
    [Inject] protected IBrowserViewportService BrowserViewport { get; set; }
}
```

### List/Grid Pattern
**Example: Queries List (`/Users/mirkobudimir/Dev/semantico/Semantico.UI/Components/Pages/Queries/Queries.razor`)**

```razor
@page "/semantico/queries"
@inherits BasePageComponent

<SemanticoPageTitle Title="Queries"/>
<MudContainer Class="my-4 px-4">
    <SemanticoPageHeader Icon="@Icons.Material.Filled.QueryBuilder" 
                        Title="Queries" 
                        ButtonText="New query" 
                        OnClick="@NewQuery"/>
    <SemanticoPageAlert Text="Description text"/>
    <MudDataGrid @ref="_dataGrid" 
                ServerData="ServerReload" 
                T="QueryData" 
                RowClick="OnRowClick" 
                Hover="true">
        <!-- Column definitions -->
    </MudDataGrid>
</MudContainer>
```

**Key Components:**
- `SemanticoPageTitle`: Sets page title
- `SemanticoPageHeader`: Provides consistent header with action button
- `SemanticoPageAlert`: Info banner
- `MudDataGrid`: Server-side data grid with pagination

### Dialog Pattern
**Example: Add Project Dialog (`/Users/mirkobudimir/Dev/semantico/Semantico.UI/Components/Pages/Projects/AddProjectDialog.razor`)**

```razor
<MudDialog Class="pb-2">
    <TitleContent>
        <MudText Typo="Typo.h6">
            <MudIcon Icon="@Icons.Material.Filled.Add" Class="mr-1 mb-n1" />
            Add project
        </MudText>
    </TitleContent>
    <DialogContent>
        <MudForm @ref="@_form" @bind-IsValid="_isFormValid">
            <!-- Form fields -->
        </MudForm>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="@Cancel" Variant="Variant.Filled">Cancel</MudButton>
        <DialogSaveButton Disabled="!_isFormValid" OnClick="@Submit"/>
    </DialogActions>
</MudDialog>
```

**Pattern Elements:**
- Form validation with `MudForm`
- Custom `DialogSaveButton` component
- Consistent styling and layout
- Cancel/Submit actions

### Service Integration Pattern
```csharp
// Service injection and usage
@inject IProjectService _service
@inject IDialogService DialogService

private async Task<GridData<ProjectListData>> ServerReload(GridState<ProjectListData> state)
{
    var list = await _service.GetProjects(null, CancellationToken.None);
    return new GridData<ProjectListData>
    {
        TotalItems = list.Count(),
        Items = list
    };
}
```

## Extension Points for Migration Feature

### 1. Service Layer Extensions

**Pattern**: Add new service following existing patterns
```csharp
// New service interface
public interface IMigrationService
{
    Task<BaseResponse> CreateMigrationJob(MigrationJobData jobData, CancellationToken cancellationToken);
    Task<PagedList<MigrationJobData>> GetMigrationJobs(GetMigrationJobsRequest request, CancellationToken cancellationToken);
    Task<MigrationJobDetailsData> GetMigrationJobDetails(int jobId, CancellationToken cancellationToken);
    Task<MigrationExecutionResult> ExecuteMigrationJob(int jobId, CancellationToken cancellationToken);
}

// Register in ServiceConfiguration.cs
services.TryAddTransient<IMigrationService, MigrationService>();
```

### 2. Entity Extensions

**Pattern**: New entities following existing hierarchy
```csharp
internal class MigrationJob : ArchivableBaseEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required int SourceProjectId { get; set; }
    public required int DestinationProjectId { get; set; }
    public required string SourceQuery { get; set; }
    public required MigrationStrategy Strategy { get; set; }
    
    // Navigation properties
    public Project SourceProject { get; set; } = null!;
    public Project DestinationProject { get; set; } = null!;
    public List<MigrationExecution> Executions { get; set; } = new();
}

internal class MigrationExecution : BaseEntity
{
    public required int MigrationJobId { get; set; }
    public required int RecordsProcessed { get; set; }
    public required MigrationStatus Status { get; set; }
    public required double ExecutionTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
    
    public MigrationJob MigrationJob { get; set; } = null!;
}
```

### 3. UI Component Extensions

**Pattern**: Follow existing page structure
- Migration Jobs List: `/semantico/migrations`
- Migration Job Details: `/semantico/migrations/details/{id}`  
- Add Migration Dialog: `AddMigrationJobDialog.razor`

**Reusable Components:**
- `SemanticoPageHeader` - for consistent headers
- `SemanticoPageAlert` - for informational messages
- `BasePageComponent` - for navigation and history
- `MudDataGrid` - for job listing
- Dialog pattern - for job creation/editing

### 4. Adapter Pattern Extensions

**Pattern**: Extend existing adapter system for migration-specific notifications
```csharp
internal class MigrationAdapter : IAdapter
{
    public NotificationType NotificationType => NotificationType.Migration;
    
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        // Migration-specific notification logic
        // Report on migration progress, completion, errors
    }
}

// Register in ServiceConfiguration.cs
services.AddSingleton<IAdapter, MigrationAdapter>();
```

### 5. Job Execution Extensions

**Pattern**: Extend existing `JobService` or create `MigrationJobService`
```csharp
internal class MigrationJobService(IDbContextFactory<SemanticoContext> contextFactory, 
                                  IQueryService queryService, 
                                  INotificationService notificationService) : IMigrationJobService
{
    public async Task ExecuteMigrationJob(int migrationJobId)
    {
        // 1. Load migration job configuration
        // 2. Execute source query to extract data
        // 3. Apply transformation logic
        // 4. Execute destination insert/update
        // 5. Record execution history
        // 6. Send notifications
    }
}
```

## Recommended Implementation Strategy

Based on the architecture analysis, the migration feature should:

1. **Leverage Existing Query Engine**: Use the multi-step, cross-project query execution system for ETL operations
2. **Extend Entity Model**: Add new migration-specific entities following existing patterns
3. **Reuse UI Patterns**: Follow established list/detail/dialog patterns for consistency
4. **Integrate with Notifications**: Use existing notification system for migration status updates
5. **Utilize Project System**: Leverage existing project entities for source/destination configuration

The architecture is well-suited for extension and provides solid foundations for building the migration feature while maintaining consistency with existing patterns.