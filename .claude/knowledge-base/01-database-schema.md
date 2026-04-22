# Beacon Database Schema & Entities

## Overview

The Beacon database supports a multi-step query execution and scheduling system with data migration capabilities, subscription-based notifications, and rich metadata tracking. The architecture follows Clean Architecture principles with soft-delete semantics via archiving.

## Base Classes

### BaseEntity
**Location:** `Beacon.Core/Data/Entities/Base/BaseEntity.cs`

```csharp
public abstract class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
}
```

### ArchivableBaseEntity
**Location:** `Beacon.Core/Data/Entities/Base/ArchivableBaseEntity.cs`

```csharp
public abstract class ArchivableBaseEntity : BaseEntity
{
    public DateTime? ArchivedTime { get; set; } = null!;
    public void Archive() { ArchivedTime = DateTime.UtcNow; }
    public void Unarchive() { ArchivedTime = null; }
}
```

**Pattern:** Automatic EF Core query filters exclude archived records (`ArchivedTime == null`).

**Used by:** Query, Subscription, QueryParameter, SubscriptionParameter, Recipient, DataSource, QueryTask, MigrationJob, DatabaseMetadata

---

## Enumerations

| Enum | Values | Purpose |
|------|--------|---------|
| **FileType** | Csv (1), Xlsx (2) | Attachment format for subscription results |
| **MigrationMode** | Insert (1), Upsert (2), Truncate (3) | Data migration operation mode |
| **EntityType** | Task (1), Subscription (2), Query (3), MigrationJob (4) | Entity types for comments/audit |
| **DatabaseEngineType** | PostgreSQL (1), MSSQL (2), MySQL (3), SQLite (4) | Supported database engines |
| **ParameterType** | Number (1), DateTime (2), String (3) | Query parameter data types |
| **NotificationStatus** | Created (1), NotificationSent (2), NotificationSilenced (3), NoResults (4), Timeout (5) | Query execution result status |
| **MigrationStatus** | Queued (1), Running (2), Completed (3), Failed (4), Cancelled (5), PartialSuccess (6) | Migration job execution status |
| **NotificationType** | Teams (1), Email (2), Jira (3), Slack (4) | Notification delivery channels |

---

## Core Entities

### Query (ArchivableBaseEntity)
**File:** `Beacon.Core/Data/Entities/Query.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| Name | string | Yes | Query name |
| Description | string? | No | Query description |
| FinalQuery | string? | No | Final query against SQLite with @result# references |

**Relationships:**
- 1:N → Subscription
- 1:N → QueryStep

**Computed Properties (IMultiStepWorkflow):**
- `IsMultiStep` - More than one step
- `IsCrossDataSource` - Steps target different data sources
- `IsCrossDatabase` - Steps use different database engines
- `DataSourceIds` - Distinct data source IDs from steps
- `DatabaseEngines` - Distinct database engines from steps

---

### QueryStep (BaseEntity)
**File:** `Beacon.Core/Data/Entities/QueryStep.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| QueryId | int | Yes | FK to Query |
| DataSourceId | int | Yes | FK to DataSource (each step can target different DS) |
| StepOrder | int | Yes | Execution order within query |
| SqlValue | string | Yes | SQL to execute |
| Name | string? | No | Step name |
| Description | string? | No | Step description |

**Relationships:**
- N:1 → Query
- N:1 → DataSource
- 1:N → QueryStepParameter

---

### QueryStepParameter (BaseEntity)
**File:** `Beacon.Core/Data/Entities/QueryStepParameter.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| QueryStepId | int | Yes | FK to QueryStep |
| Name | string | Yes | Parameter name |
| Type | ParameterType | Yes | Number/DateTime/String |
| Description | string? | No | Description |
| Placeholder | string? | No | UI placeholder |

---

### DataSource (ArchivableBaseEntity)
**File:** `Beacon.Core/Data/Entities/DataSource.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| Name | string | Yes | Data source name |
| ConnectionString | string | Yes | Database connection string (encrypted) |
| DatabaseEngineType | DatabaseEngineType | Yes | PostgreSQL/MSSQL/MySQL/SQLite |

**Relationships:**
- 1:N → QueryStep
- 1:N → DatabaseMetadata (cascade delete)
- 1:N → MigrationJob (source and destination)

---

### Subscription (ArchivableBaseEntity)
**File:** `Beacon.Core/Data/Entities/Subscription.cs`

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| QueryId | int | Yes | - | FK to Query |
| CronExpression | string | Yes | - | Cron schedule |
| MaxRows | int? | No | null | Result limit (null = no limit) |
| IncludeAttachment | bool | - | true | Include file attachment |
| ResultAttachmentType | FileType? | No | null | Csv/Xlsx |
| ShowQuery | bool | - | true | Show SQL in notification |
| TimeoutSeconds | int? | No | null | Query execution timeout |
| StoreResults | bool | - | false | Store JSON results in notification |
| CreateTasks | bool | - | false | Auto-create tasks from executions |

**Relationships:**
- N:1 → Query
- N:M ↔ Recipient (implicit join table)
- 1:N → SubscriptionParameter
- 1:N → QueryExecutionHistory
- 1:1 → QueryTask (via unique constraint)

---

### Recipient (ArchivableBaseEntity)
**File:** `Beacon.Core/Data/Entities/Recipient.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| Name | string | Yes | Recipient name |
| Description | string? | No | Description |
| Destination | string | Yes | Email/Teams URL/Slack webhook/Jira URL |
| NotificationType | NotificationType | Yes | Teams/Email/Jira/Slack |

**Destination Formats:**
- **Email:** `user@domain.com`
- **Teams:** `https://outlook.office.com/webhook/...`
- **Slack:** `https://hooks.slack.com/services/...`
- **Jira:** `domain;project;email;apikey`

---

### QueryExecutionHistory (BaseEntity)
**File:** `Beacon.Core/Data/Entities/QueryExecutionHistory.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| SubscriptionId | int | Yes | FK to Subscription |
| ResultCount | int | Yes | Row count returned |
| CompiledSql | string | Yes | Final executed SQL |
| NotificationStatus | NotificationStatus | Yes | Created/Sent/Silenced/NoResults/Timeout |
| ExecutionTimeMs | double | Yes | Execution duration in milliseconds |
| Results | string? | No | JSON serialized results (if StoreResults=true) |

**Relationships:**
- N:1 → Subscription
- 1:N → Notification

---

### Notification (BaseEntity)
**File:** `Beacon.Core/Data/Entities/Notification.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| QueryExecutionHistoryId | int | Yes | FK to QueryExecutionHistory |
| RecipientId | int | Yes | FK to Recipient |
| Type | NotificationType | Yes | Teams/Email/Jira/Slack |
| SentAt | DateTime | Yes | Notification send time |
| Results | string? | No | Query results (legacy) |
| TaskId | int? | No | FK to QueryTask (optional) |

---

### QueryTask (ArchivableBaseEntity)
**File:** `Beacon.Core/Data/Entities/QueryTask.cs`

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| SubscriptionId | int | Yes | - | FK to Subscription (UNIQUE) |
| LatestResultCount | int | Yes | - | Row count from latest execution |
| LastNotificationAt | DateTime? | No | - | Last notification send time |
| Resolved | bool | - | false | Task resolution status |
| ResolvedAt | DateTime? | No | - | Task resolution timestamp |
| ResolvedByUserId | string? | No | - | User who resolved |
| ResolutionNotes | string? | No | - | Resolution notes (max 2000 chars) |

**Indexes:**
- UNIQUE on SubscriptionId (one task per subscription)
- Composite on (Resolved, CreatedTime)

**Auto-Resolution:** Task auto-resolves when query returns 0 results.

---

### Comment (BaseEntity)
**File:** `Beacon.Core/Data/Entities/Comment.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| EntityType | EntityType | Yes | Task/Subscription/Query/MigrationJob |
| EntityId | int | Yes | Reference to entity |
| Content | string | Yes | Comment text (max 4000 chars) |
| UserId | string? | No | User identifier |
| UserName | string? | No | User display name |

**Pattern:** Generic entity reference via EntityType enum + EntityId.

---

## Data Migration Entities

### MigrationJob (ArchivableBaseEntity)
**File:** `Beacon.Core/Data/Entities/DataMigration/MigrationJob.cs`

| Property | Type | Required | Default | Notes |
|----------|------|----------|---------|-------|
| Name | string | Yes | - | Job name (max 200) |
| Description | string | Yes | - | Job description (max 1000) |
| DataSourceId | int | Yes | - | FK to source DataSource |
| QueryText | string | Yes | - | Query or JSON-serialized steps |
| DestinationDataSourceId | int | Yes | - | FK to destination DataSource |
| DestinationTable | string | Yes | - | Target table name (max 100) |
| Mode | MigrationMode | - | Insert | Insert/Upsert/Truncate |
| IsEnabled | bool | - | true | Execution enablement |
| Schedule | string? | No | - | Cron expression (max 50) |
| MaxRetries | int | - | 3 | Retry limit |
| TimeoutMinutes | int | - | 30 | Execution timeout |
| ValidateBeforeExecution | bool | - | true | Pre-validation |
| TransformationScript | string? | No | - | @result transformation syntax |

---

### MigrationExecutionHistory (BaseEntity)
**File:** `Beacon.Core/Data/Entities/DataMigration/MigrationExecution.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| MigrationJobId | int | Yes | FK to MigrationJob |
| StartedAt | DateTime | Yes | Execution start time |
| CompletedAt | DateTime? | No | Execution completion time |
| Status | MigrationStatus | - | Queued/Running/Completed/Failed/Cancelled/PartialSuccess |
| ErrorMessage | string? | No | Error details (max 4000) |
| SourceRowsRead | int | - | Source row count |
| DestinationRowsWritten | int | - | Rows written to destination |
| RowsSkipped | int | - | Skipped rows |
| RowsFailed | int | - | Failed rows |
| ExecutedQuery | string | Yes | Actual executed query |
| RetryAttempt | int | - | Retry count |
| ParentExecutionId | int? | No | FK to parent execution (retry tracking) |

---

## Database Metadata Entities

### DatabaseMetadata (ArchivableBaseEntity)
**File:** `Beacon.Core/Data/Entities/Metadata/DatabaseMetadata.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| DataSourceId | int | Yes | FK to DataSource |
| SchemaName | string | Yes | Database schema name (max 100) |
| TableName | string | Yes | Table name (max 200) |
| TableDescription | string? | No | Table description (max 1000) |
| LastRefreshed | DateTime | - | Cache timestamp |

**Unique Constraint:** (DataSourceId, SchemaName, TableName)

### ColumnMetadata (BaseEntity)
**File:** `Beacon.Core/Data/Entities/Metadata/ColumnMetadata.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| DatabaseMetadataId | int | Yes | FK to DatabaseMetadata |
| ColumnName | string | Yes | Column name (max 200) |
| DataType | string | Yes | Data type (max 100) |
| IsNullable | bool | - | Nullable constraint |
| IsPrimaryKey | bool | - | Primary key flag |
| IsForeignKey | bool | - | Foreign key flag |
| OrdinalPosition | int | - | Column order in table |
| ForeignKeyTable | string? | No | Referenced table (max 200) |
| ForeignKeyColumn | string? | No | Referenced column (max 200) |
| DefaultValue | string? | No | Column default (max 500) |
| MaxLength | int? | No | String max length |
| Description | string? | No | Column description (max 1000) |

### IndexMetadata (BaseEntity)
**File:** `Beacon.Core/Data/Entities/Metadata/IndexMetadata.cs`

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| DatabaseMetadataId | int | Yes | FK to DatabaseMetadata |
| IndexName | string | Yes | Index name (max 200) |
| IsUnique | bool | - | Unique constraint flag |
| IsPrimaryKey | bool | - | Primary key flag |
| Columns | string[] | Yes | Array of column names |

---

## Entity Relationship Diagram

```
┌─────────────────┐     1:N     ┌─────────────────┐     1:N     ┌─────────────────────────┐
│    DataSource   │─────────────│    QueryStep    │─────────────│  QueryStepParameter     │
└─────────────────┘             └─────────────────┘             └─────────────────────────┘
        │                              │ N:1
        │                              ▼
        │                       ┌─────────────────┐     1:N     ┌─────────────────┐
        │                       │      Query      │─────────────│   Subscription  │
        │                       └─────────────────┘             └─────────────────┘
        │                                                              │
        │         ┌────────────────────────────────────────────────────┤
        │         │ N:M                     │ 1:1               │ 1:N
        │         ▼                         ▼                   ▼
        │   ┌───────────┐           ┌──────────────┐   ┌───────────────────────────┐
        │   │ Recipient │           │  QueryTask   │   │ SubscriptionParameter     │
        │   └───────────┘           └──────────────┘   └───────────────────────────┘
        │         │ 1:N                    │ 1:N
        │         │                        │
        │         ▼                        ▼
        │   ┌────────────────────────────────────────┐
        │   │           Notification                 │
        │   └────────────────────────────────────────┘
        │                    ▲ N:1
        │                    │
        │   ┌────────────────────────────────────────┐
        │   │      QueryExecutionHistory             │
        │   └────────────────────────────────────────┘
        │                    ▲ N:1
        │                    │
        └────────────────────┘

┌─────────────────┐     1:N     ┌──────────────────────────┐
│   DataSource    │─────────────│     MigrationJob         │
└─────────────────┘             └──────────────────────────┘
                                          │ 1:N
                                          ▼
                                ┌──────────────────────────┐
                                │ MigrationExecutionHistory│
                                └──────────────────────────┘

┌─────────────────┐     1:N     ┌──────────────────────────┐
│   DataSource    │─────────────│    DatabaseMetadata      │
└─────────────────┘             └──────────────────────────┘
                                          │ 1:N        │ 1:N
                                          ▼            ▼
                                ┌──────────────┐ ┌──────────────┐
                                │ColumnMetadata│ │IndexMetadata │
                                └──────────────┘ └──────────────┘
```

---

## Database Context

**File:** `Beacon.Core/Data/BeaconContext.cs`

**Default Schema:** `"beacon"` (configurable via constructor)

**Key Configuration:**
- Global query filters for soft-delete pattern
- Cascade delete for metadata entities
- Unique constraints on QueryTask.SubscriptionId
- Composite indexes for performance

**DbSet Properties:**
- Subscriptions, SubscriptionParameters
- Queries, QueryParameters, QuerySteps, QueryStepParameters
- DataSources, Recipients, Notifications
- QueryExecutionHistory, QueryTasks
- MigrationJobs, MigrationExecutions
- DatabaseMetadata, ColumnMetadata, IndexMetadata
- Comments
