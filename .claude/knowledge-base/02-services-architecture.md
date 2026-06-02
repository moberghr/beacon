# Beacon Services Architecture

## Overview

Beacon follows Clean Architecture with services implementing business logic. All services use `IDbContextFactory<BeaconContext>` for database access and follow the CQRS-light pattern.

**Common Patterns:**
- Services are registered as `internal class` with public interfaces
- Primary constructor injection for dependencies
- Async operations with CancellationToken support
- `WhereIf()` extension for conditional filtering
- `ToPagedListAsync()` for pagination

---

## QueryService

**Interface:** `IQueryService`
**File:** `Beacon.Core/Services/QueryService.cs`

### Responsibilities
- Query CRUD operations with multi-step support
- Query execution against multiple data sources
- Cross-database query orchestration via SQLite virtual tables
- Step-based preview execution

### Key Methods

| Method | Purpose |
|--------|---------|
| `CreateQuery(QueryData)` | Create query with steps, validates SQL, creates parameters |
| `UpdateQuery(QueryData)` | Update query and steps, handles backward compatibility |
| `DeleteQuery(int)` | Archive query, cascade-archive subscriptions |
| `GetQueries(GetQueriesRequest)` | Paginated query list with step info |
| `GetQueryDetails(int)` | Full query details with execution history |
| `ExecuteQuery(int subscriptionId)` | Execute via subscription (scheduled jobs) |
| `ExecuteQueryAdvanced(int, string?, List<ParameterValue>?)` | Advanced multi-step execution |
| `PreviewQueryStep(int, int, List<ParameterValue>?)` | Preview single step with parameters |
| `AddQueryStep(int, QueryStepData)` | Add step to existing query |
| `UpdateQueryStep(int, int, QueryStepData)` | Update specific step |
| `DeleteQueryStep(int, int)` | Remove step (min 1 required) |

### Query Execution Flow

```
ExecuteQuery(subscriptionId)
    │
    ▼
GetQueryWithSteps() ─── Load query, steps, data sources, parameters
    │
    ▼
ExecuteQuerySteps() ─── For each step in order:
    │                       │
    │                       ├── ExecuteStep() against step's DataSource
    │                       │       │
    │                       │       └── ExecuteQueryAsync() via Dapper
    │                       │
    │                       └── VirtualTableManager.AddVirtualTable(@resultN)
    │
    ▼
HasFinalQuery? ─── Yes ───► ExecuteFinalQuery() against SQLite virtual tables
    │
    No
    │
    ▼
Return single step result as QueryResult
```

### Cross-Database Support

```csharp
// VirtualTableManager creates SQLite in-memory database
using var virtualTableManager = new VirtualTableManager(logger);

// Each step result is stored as a virtual table
virtualTableManager.AddVirtualTable("@result1", step1Results, dataSourceInfo);
virtualTableManager.AddVirtualTable("@result2", step2Results, dataSourceInfo);

// Final query can JOIN across step results
var finalResult = await virtualTableManager.ExecuteFinalQueryWithInMemoryDatabase(
    "SELECT r1.*, r2.status FROM @result1 r1 JOIN @result2 r2 ON r1.id = r2.id",
    logger,
    cancellationToken);
```

### Dependencies
- `IDbContextFactory<BeaconContext>` - Database access
- `IEncryptionService` - Connection string decryption
- `ILogger<QueryService>` - Logging
- `ILoggerFactory` - Logger creation for VirtualTableManager

---

## SubscriptionService

**Interface:** `ISubscriptionService`
**File:** `Beacon.Core/Services/SubscriptionService.cs`

### Responsibilities
- Subscription CRUD with cron schedule management
- Recipient assignment (many-to-many)
- Parameter value storage
- Job scheduler integration

### Key Methods

| Method | Purpose |
|--------|---------|
| `CreateSubscription(SubscriptionData)` | Create subscription, validate cron, register with scheduler |
| `UpdateSubscription(SubscriptionData)` | Update subscription, re-register job if cron changed |
| `DeleteSubscription(int)` | Archive subscription, remove scheduled job |
| `GetSubscriptions(filters)` | List subscriptions with optional filters |
| `GetSubscriptionDetails(int)` | Full subscription details |
| `AddRecipients(int, List<int>)` | Add recipients to subscription |
| `RemoveRecipient(int, int)` | Remove recipient from subscription |

### Validation Rules

```csharp
// Cron expression validation (via Cronos)
CronExpression.Parse(subscriptionData.CronExpression);

// Recipient requirement (unless CreateTasks enabled)
if (!subscriptionData.CreateTasks && !subscriptionData.Recipients.Any())
{
    throw new BeaconException("At least one recipient is required when 'Create Tasks' is not enabled");
}

// Parameter validation
SubscriptionValidator.ValidateParameters(subscriptionData.Parameters, queryParams);
```

### Scheduler Integration

```csharp
// Register job with scheduler
beaconScheduler.AddOrUpdate(
    subscription.Id,
    $"{query.Name}: {subscription.Id}",
    subscription.CronExpression);

// Remove on delete
beaconScheduler.Remove(subscription.Id, $"{query.Name}: {subscription.Id}");
```

### Dependencies
- `IDbContextFactory<BeaconContext>` - Database access
- `IBeaconScheduler` - Job scheduling abstraction

---

## NotificationService

**Interface:** `INotificationService`
**File:** `Beacon.Core/Services/NotificationService.cs`

### Responsibilities
- Dispatch notifications via adapters
- Query execution history retrieval
- Notification statistics aggregation

### Key Methods

| Method | Purpose |
|--------|---------|
| `SendNotification(RecipientQueryResult, int?)` | Route to adapter by NotificationType |
| `GetQueryExecutionHistory(request)` | Paginated execution history |
| `GetNotificationStatistics()` | 30-day aggregated statistics |
| `GetNotificationDetails(int)` | Single notification details |
| `GetQueryExecutionHistoryDetails(int)` | Execution details with notifications and tasks |

### Notification Dispatch

```csharp
public async Task SendNotification(RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount)
{
    // Task creation is handled in JobService.ExecuteQuery, not here
    var adapter = adapterFactory.GetAdapterService(recipientQueryResult.RecipientNotificationType);
    await adapter.SendNotificationAsync(recipientQueryResult, lastExecutedQueryResultCount);
}
```

### Statistics Query

```csharp
// Get 30-day statistics combining query executions and migrations
var queryStats = await context.QueryExecutionHistory
    .Where(x => x.CreatedTime >= cutoffDate)
    .GroupBy(x => x.CreatedTime.Date)
    .Select(x => new { Date, TotalQueries, NotificationsSent })
    .ToListAsync();
```

### Dependencies
- `IDbContextFactory<BeaconContext>` - Database access
- `AdapterFactory` - Notification adapter routing

---

## TaskService

**Interface:** `ITaskService`
**File:** `Beacon.Core/Services/TaskService.cs`

### Responsibilities
- Task lifecycle management (create, update, resolve, reopen)
- Auto-resolution when query returns 0 results
- Task comments for collaboration
- Result count history tracking

### Key Methods

| Method | Purpose |
|--------|---------|
| `CreateTask(int notificationId, int subscriptionId, int resultCount)` | Create task from notification |
| `CreateOrUpdateTask(int subscriptionId, int resultCount)` | Upsert task, auto-resolve if 0 results |
| `ResolveTask(int, string?, string?)` | Manual resolution with notes |
| `ReopenTask(int)` | Reopen resolved task |
| `GetTasks(GetTasksRequest)` | Paginated task list |
| `GetTaskDetails(int)` | Full task details with notifications |
| `GetTaskExecutionHistory(int)` | Last 50 executions for task's subscription |
| `GetRelatedTasks(int)` | Tasks from same query |
| `GetResultCountHistory(int)` | Result count over time for charts |
| `GetTaskComments(int)` | Task comments |
| `AddTaskComment(int, string, string?, string?)` | Add comment to task |

### Auto-Resolution Logic

```csharp
private static void UpdateTaskWithResultCount(QueryTask task, int resultCount)
{
    task.LatestResultCount = resultCount;
    task.LastNotificationAt = DateTime.UtcNow;

    if (resultCount == 0)
    {
        task.Resolved = true;
        task.ResolvedAt = DateTime.UtcNow;
        task.ResolutionNotes = "Auto-resolved: Query returned 0 results";
    }
}
```

### Task Creation Flow

```
CreateOrUpdateTask(subscriptionId, resultCount)
    │
    ▼
FindUnresolvedTaskAsync() ─── Existing task?
    │                              │
    Yes                           No
    │                              │
    ▼                              ▼
UpdateTaskWithResultCount()   resultCount == 0?
    │                              │
    │                         Yes  │  No
    │                          │   │
    │                          ▼   ▼
    │                     Return 0  CreateNewTask()
    │                              │
    ▼                              ▼
Return task.Id               Return task.Id
```

### Dependencies
- `IDbContextFactory<BeaconContext>` - Database access
- `ILogger<TaskService>` - Logging

---

## RecipientService

**Interface:** `IRecipientService`
**File:** `Beacon.Core/Services/RecipientService.cs`

### Responsibilities
- Recipient CRUD operations
- Validation before deletion (no active subscriptions)

### Key Methods

| Method | Purpose |
|--------|---------|
| `CreateRecipient(RecipientData)` | Create new recipient |
| `UpdateRecipient(RecipientData)` | Update recipient details |
| `DeleteRecipient(int)` | Archive recipient (fails if has subscriptions) |
| `GetRecipients(int?, string?)` | List recipients with optional search |

### Deletion Protection

```csharp
public async Task DeleteRecipient(int recipientId, CancellationToken cancellationToken)
{
    var recipient = await context.Recipients.Where(x => x.Id == recipientId).SingleAsync();

    if (recipient.Subscriptions.Count > 0)
    {
        throw new BeaconException("Unable to remove recipient due to existing subscriptions");
    }

    recipient.Archive();
    await context.SaveChangesAsync(cancellationToken);
}
```

### Dependencies
- `IDbContextFactory<BeaconContext>` - Database access

---

## DataSourceService

**Interface:** `IDataSourceService`
**File:** `Beacon.Core/Services/DataSourceService.cs`

### Responsibilities
- Data source CRUD with connection string encryption
- Database metadata introspection
- Connection testing

### Key Methods

| Method | Purpose |
|--------|---------|
| `CreateDataSource(DataSourceData)` | Create with encrypted connection string |
| `UpdateDataSource(DataSourceData)` | Update data source |
| `DeleteDataSource(int)` | Archive data source |
| `GetDataSources()` | List all data sources |
| `TestConnection(int)` | Verify connectivity |
| `RefreshMetadata(int)` | Reload schema metadata |

### Connection String Handling

```csharp
// Encrypt on save
dataSource.ConnectionString = encryptionService.Encrypt(data.ConnectionString);

// Decrypt on use
var decryptedCs = encryptionService.Decrypt(dataSource.ConnectionString);
```

### Dependencies
- `IDbContextFactory<BeaconContext>` - Database access
- `IEncryptionService` - Connection string encryption
- `IDatabaseMetadataService` - Schema introspection

---

## MigrationService

**Interface:** `IMigrationService`
**File:** `Beacon.Core/Services/MigrationService.cs`

### Responsibilities
- Migration job CRUD
- ETL execution with multiple modes (Insert/Upsert/Truncate)
- Execution history tracking
- Row-level error handling

### Key Methods

| Method | Purpose |
|--------|---------|
| `CreateMigrationJob(MigrationJobData)` | Create migration job |
| `UpdateMigrationJob(MigrationJobData)` | Update migration job |
| `DeleteMigrationJob(int)` | Archive migration job |
| `ExecuteMigration(int)` | Run migration job |
| `GetMigrationJobs()` | List migration jobs |
| `GetMigrationExecutions(int)` | Execution history for job |

### Migration Modes

| Mode | Behavior |
|------|----------|
| **Insert** | Insert all rows, fail on duplicates |
| **Upsert** | Insert new, update existing by key |
| **Truncate** | Delete all destination data, then insert |

### Dependencies
- `IDbContextFactory<BeaconContext>` - Database access
- `IEncryptionService` - Connection string decryption
- `IQueryService` - Query execution for source data

---

## DatabaseMetadataService

**Interface:** `IDatabaseMetadataService`
**File:** `Beacon.Core/Services/DatabaseMetadataService.cs`

### Responsibilities
- Database schema introspection
- Metadata caching in database
- Column and index discovery

### Key Methods

| Method | Purpose |
|--------|---------|
| `GetMetadata(int dataSourceId)` | Get cached metadata |
| `RefreshMetadata(int dataSourceId)` | Re-introspect schema |
| `GetTableColumns(int, string, string)` | Get columns for table |
| `GetTableIndexes(int, string, string)` | Get indexes for table |

### Engine-Specific Queries

The service uses Dapper with engine-specific information schema queries:

```csharp
// PostgreSQL
SELECT table_schema, table_name, column_name, data_type, is_nullable, ...
FROM information_schema.columns WHERE table_catalog = @catalog

// SQL Server
SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, ...
FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_CATALOG = @catalog
```

### Dependencies
- `IDbContextFactory<BeaconContext>` - Database access
- `IEncryptionService` - Connection string decryption

---

## CommentService

**Interface:** `ICommentService`
**File:** `Beacon.Core/Services/CommentService.cs`

### Responsibilities
- Generic comment system for entities
- Supports Task, Subscription, Query, MigrationJob

### Key Methods

| Method | Purpose |
|--------|---------|
| `GetComments(EntityType, int)` | Get comments for entity |
| `AddComment(EntityType, int, string, string?, string?)` | Add comment |
| `DeleteComment(int)` | Remove comment |

### Generic Entity Pattern

```csharp
var comments = await context.Comments
    .Where(c => c.EntityType == entityType && c.EntityId == entityId)
    .OrderByDescending(c => c.CreatedTime)
    .Select(c => new CommentData(c.Id, c.Content, c.UserName, c.CreatedTime))
    .ToListAsync();
```

---

## Service Interfaces Summary

| Interface | Implementation | Registration |
|-----------|----------------|--------------|
| `IQueryService` | `QueryService` | Scoped |
| `ISubscriptionService` | `SubscriptionService` | Scoped |
| `INotificationService` | `NotificationService` | Scoped |
| `ITaskService` | `TaskService` | Scoped |
| `IRecipientService` | `RecipientService` | Scoped |
| `IDataSourceService` | `DataSourceService` | Scoped |
| `IMigrationService` | `MigrationService` | Scoped |
| `IDatabaseMetadataService` | `DatabaseMetadataService` | Scoped |
| `ICommentService` | `CommentService` | Scoped |
| `IEncryptionService` | `AesEncryptionService` | Singleton |
| `IBeaconScheduler` | Consumer-provided | Scoped |

---

## Helper Classes

### QueryHelper
**File:** `Beacon.Core/Helpers/QueryHelper.cs`

```csharp
// Compile SQL with parameter substitution
public static string CompileSql(string sql, List<SubscriptionParameterData> parameters)
```

### QueryValidator
**File:** `Beacon.Core/Validators/QueryValidator.cs`

```csharp
// Check for dangerous SQL keywords (DROP, DELETE, TRUNCATE, etc.)
public static void CheckForFlaggedWords(string sql)

// Validate all declared parameters have values
public static void CheckForParameters(string sql, List<QueryParameterData> parameters)
```

### ParameterEntityFactory
**File:** `Beacon.Core/Helpers/ParameterEntityFactory.cs`

```csharp
// Create QueryStepParameter entities from DTOs
public static List<QueryStepParameter> CreateQueryStepParameters(
    List<QueryStepParameterData> parameters, int stepId)

// Create SubscriptionParameter entities from DTOs
public static List<SubscriptionParameter> CreateSubscriptionParameters(
    List<SubscriptionParameterData> parameters, int? subscriptionId = null)
```
