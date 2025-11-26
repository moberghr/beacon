# Data Model: Alerting Tasks Recipient

**Feature**: 004-alerting-tasks | **Date**: 2025-11-12

## Overview

This document defines the data model for the Tasks recipient feature, including entity schemas, relationships, validation rules, and state transitions. The model extends the existing Semantico alerting architecture following Clean Architecture and multi-provider database support principles.

## Entity Definitions

### Task Entity (NEW)

**Purpose**: Represents an internal task record generated when a subscription with a Tasks recipient executes and returns results.

**Base Class**: `BaseArchivableEntity` (provides `Id`, `CreatedTime`, `ArchivedTime`, `Archive()` method)

**Location**: `Semantico.Core/Data/Entities/Task.cs`

#### Schema

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `Id` | int | Yes | Auto | Primary key (inherited from BaseArchivableEntity) |
| `SubscriptionId` | int | Yes | - | Foreign key to Subscription entity |
| `RecipientId` | int | Yes | - | Foreign key to Recipient entity |
| `LatestResultCount` | int | Yes | - | Number of records returned by most recent query execution |
| `LastNotificationAt` | DateTime? | No | null | Timestamp of most recent notification linked to this task (UTC) |
| `Resolved` | bool | Yes | false | Whether task has been marked resolved (manually or auto-resolved when results = 0) |
| `ResolvedAt` | DateTime? | No | null | Timestamp when task was resolved (UTC) |
| `ResolvedByUserId` | string? | No | null | User ID who resolved the task (nullable for future auth integration, null for auto-resolved) |
| `ResolutionNotes` | string? | No | null | Notes documenting resolution (max 2000 characters, auto-populated for auto-resolved tasks) |
| `CreatedTime` | DateTime | Yes | Auto | Timestamp when task was first created (UTC, inherited) |
| `ArchivedTime` | DateTime? | No | null | Timestamp when task was archived (UTC, inherited) |

#### Navigation Properties

| Property | Type | Relationship | Description |
|----------|------|--------------|-------------|
| `Notifications` | List<Notification> | One-to-Many | All notifications linked to this task (tracks progression over time) |
| `Subscription` | Subscription | Many-to-One (required) | The subscription that generated this task |
| `Recipient` | Recipient | Many-to-One (required) | The Tasks recipient this task was sent to |

#### Validation Rules

- **SubscriptionId + RecipientId**: Unique constraint (one task per subscription-recipient pair)
- **SubscriptionId**: Must reference existing Subscription record (can be archived)
- **RecipientId**: Must reference existing Recipient record with NotificationType = Tasks
- **LatestResultCount**: Must be >= 0 (zero triggers auto-resolution)
- **Resolved**: Non-nullable boolean (default false, auto-set to true when resultCount = 0)
- **ResolvedAt**: Must be null if Resolved = false; must be set if Resolved = true
- **ResolvedByUserId**: Optional (null for auto-resolved or when user auth not integrated)
- **ResolutionNotes**: Max length 2000 characters; auto-populated for auto-resolved tasks

#### State Transitions

**Initial State**: `Resolved = false, ResolvedAt = null, ResolvedByUserId = null, ResolutionNotes = null`

**State: Unresolved → Resolved**
- Trigger: User clicks "Resolve Task" in UI
- Action: Set `Resolved = true`, `ResolvedAt = DateTime.UtcNow`, optionally set `ResolutionNotes`
- Validation: Task must be in Unresolved state (no-op if already resolved, or update resolution)
- Handler: `ResolveTask.Handler`

**State: Resolved → Unresolved (Reopen)**
- Trigger: User clicks "Reopen Task" in UI
- Action: Set `Resolved = false`, `ResolvedAt = null`, clear `ResolutionNotes` (or retain with note "Reopened")
- Validation: Task must be in Resolved state
- Handler: `ReopenTask.Handler`

**State: Active → Archived**
- Trigger: User archives task (future feature, not in initial scope)
- Action: Set `ArchivedTime = DateTime.UtcNow` (inherited from BaseArchivableEntity)
- Effect: Task excluded from default queries (soft delete)

#### Indexes

**Performance-critical indexes** (applied in `SemanticoContext.OnModelCreating()`):

1. **Unique Composite**: `(SubscriptionId, RecipientId)` - **CRITICAL**
   - **Use Case**: Enforce one task per subscription-recipient pair
   - **Cardinality**: Unique constraint prevents duplicate tasks
   - **Index Name**: `IX_Task_SubscriptionId_RecipientId_Unique`

2. **Composite**: `(RecipientId, Resolved, CreatedTime DESC)`
   - **Use Case**: "Show all unresolved tasks for a recipient, newest first" (most common query)
   - **Cardinality**: High selectivity (RecipientId + Resolved), good ordering

3. **Composite**: `(SubscriptionId, CreatedTime DESC)`
   - **Use Case**: Task timeline for subscription (P3 user story)
   - **Cardinality**: Medium selectivity, supports temporal queries

4. **Single**: `CreatedTime DESC`
   - **Use Case**: Global task list sorted by creation date
   - **Cardinality**: Supports pagination and date range filters

**Note**: The unique index on (SubscriptionId, RecipientId) is critical for the find-or-create logic in TasksAdapter.

#### Example Entity Code

```csharp
// Semantico.Core/Data/Entities/AlertingTask.cs
public class AlertingTask : ArchivableBaseEntity
{
    public required int SubscriptionId { get; set; }
    public required int RecipientId { get; set; }
    public required int LatestResultCount { get; set; }
    public DateTime? LastNotificationAt { get; set; }
    public bool Resolved { get; set; } = false;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByUserId { get; set; }
    public string? ResolutionNotes { get; set; }

    // Navigation properties
    public List<Notification> Notifications { get; set; } = new();
    public Subscription Subscription { get; set; } = null!;
    public Recipient Recipient { get; set; } = null!;
}
```

**Note**: Entity class is named `AlertingTask` (not `Task`) to avoid C# naming conflict with `System.Threading.Tasks.Task`, but the database table name is `Tasks` via `.ToTable("Tasks")` configuration.

---

### Notification Entity (MODIFIED)

**Purpose**: Add optional TaskId foreign key to link notifications to tasks.

**Location**: `Semantico.Core/Data/Entities/Notification.cs`

#### Schema Change

**ADDED Field**:
- `TaskId` (int?, nullable) - Foreign key to Task entity, only populated for Tasks recipient notifications

**ADDED Navigation**:
- `Task` (AlertingTask?, nullable) - Navigation property to parent task

**Migration Impact**: Requires database migration to add nullable TaskId column with foreign key constraint.

---

### NotificationType Enum (MODIFIED)

**Purpose**: Defines the type of notification recipient.

**Location**: `Semantico.Core/Data/Enums/NotificationType.cs`

#### Schema Change

**BEFORE**:
```csharp
public enum NotificationType
{
    Teams = 1,
    Email = 2,
    Jira = 3
}
```

**AFTER**:
```csharp
public enum NotificationType
{
    Teams = 1,
    Email = 2,
    Jira = 3,
    Tasks = 4  // NEW: Internal task notification type
}
```

**Migration Impact**: Enum change requires no database migration (enums are stored as integers). Existing data unaffected.

---

## Entity Relationships

### Task ↔ Notification (One-to-Many)

- **Foreign Key**: `Notification.TaskId → Task.Id` (nullable)
- **Delete Behavior**: Restrict (cannot delete Task if Notifications reference it)
- **Rationale**: One task can have multiple notifications over time, tracking the progression of query results. Similar to Jira adapter pattern where subsequent executions update the same issue.

### Task ↔ Subscription (Many-to-One, Required)

- **Foreign Key**: `Task.SubscriptionId → Subscription.Id`
- **Delete Behavior**: Restrict (cannot delete Subscription if active Tasks reference it)
- **Rationale**: Tasks track subscription execution history; subscription can be archived but not hard-deleted while tasks exist
- **Note**: Denormalized from `Notification.QueryExecutionHistory.Subscription` for query performance

### Task ↔ Recipient (Many-to-One, Required)

- **Foreign Key**: `Task.RecipientId → Recipient.Id`
- **Delete Behavior**: Restrict (cannot delete Recipient if Tasks reference it)
- **Rationale**: Tasks are sent to specific Tasks recipients; recipient can be archived but not hard-deleted
- **Note**: Denormalized from `Notification.Recipient` for query performance

### Notification → Task (Many-to-One, Optional)

- **Foreign Key**: `Notification.TaskId` (nullable, only set for Tasks recipient notifications)
- **Inverse Navigation**: `Notification.Task` (optional navigation property)
- **Rationale**: Not all notifications are linked to tasks (only those sent to Tasks recipients). Multiple notifications can link to the same task, tracking the progression over time.

---

## Database Configuration

### EF Core Configuration (SemanticoContext.OnModelCreating)

```csharp
// Semantico.Core/Data/SemanticoContext.cs - OnModelCreating method additions

modelBuilder.Entity<Task>(entity =>
{
    // Composite index for filtering by recipient and resolution status
    entity.HasIndex(t => new { t.RecipientId, t.Resolved, t.CreatedTime })
          .HasDatabaseName("IX_Task_RecipientId_Resolved_CreatedTime");

    // Composite index for subscription timeline queries
    entity.HasIndex(t => new { t.SubscriptionId, t.CreatedTime })
          .HasDatabaseName("IX_Task_SubscriptionId_CreatedTime");

    // Unique index for notification relationship (one-to-one)
    entity.HasIndex(t => t.NotificationId)
          .IsUnique()
          .HasDatabaseName("IX_Task_NotificationId");

    // Global ordering index
    entity.HasIndex(t => t.CreatedTime)
          .HasDatabaseName("IX_Task_CreatedTime");

    // Field constraints
    entity.Property(t => t.ResolutionNotes)
          .HasMaxLength(2000)
          .IsUnicode(true);

    // Required navigation properties
    entity.HasOne(t => t.Notification)
          .WithOne()
          .HasForeignKey<Task>(t => t.NotificationId)
          .OnDelete(DeleteBehavior.Restrict);

    entity.HasOne(t => t.Subscription)
          .WithMany()
          .HasForeignKey(t => t.SubscriptionId)
          .OnDelete(DeleteBehavior.Restrict);

    entity.HasOne(t => t.Recipient)
          .WithMany()
          .HasForeignKey(t => t.RecipientId)
          .OnDelete(DeleteBehavior.Restrict);
});

// Add DbSet
public DbSet<Task> Tasks => Set<Task>();
```

### Migration Notes

**PostgreSQL Migration** (`Semantico.Core.PostgreSql`):
- Command: `dotnet ef migrations add AddTaskEntity --project Semantico.Core.PostgreSql --startup-project Semantico.SampleProject`
- Generates: `[Timestamp]_AddTaskEntity.cs` with schema-agnostic DDL
- Validation: Ensure no hardcoded `"semantico"."Tasks"` references

**SQL Server Migration** (`Semantico.Core.SqlServer`):
- Command: `dotnet ef migrations add AddTaskEntity --project Semantico.Core.SqlServer --startup-project Semantico.SampleProject`
- Generates: `[Timestamp]_AddTaskEntity.cs` with schema-agnostic DDL
- Differences from PostgreSQL: `nvarchar(2000)` vs `text`, `datetime2` vs `timestamp`

**Schema Application**: Runtime via `modelBuilder.HasDefaultSchema(schema)` in `SemanticoContext.OnModelCreating()`

---

## Data Transfer Objects (DTOs)

### TaskData (List Item)

**Purpose**: Lightweight DTO for task list display.

**Location**: `Semantico.Core/DTOs/TaskData.cs`

```csharp
public record TaskData
{
    public required int Id { get; init; }
    public required string SubscriptionName { get; init; }
    public required string QueryName { get; init; }
    public required string RecipientName { get; init; }
    public required int ResultCount { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required bool Resolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedByUserName { get; init; }
}
```

**Mapping**: Requires joins to Subscription, Query, Recipient tables

---

### TaskDetailsData (Detail View)

**Purpose**: Comprehensive DTO for task details page.

**Location**: `Semantico.Core/DTOs/TaskDetailsData.cs`

```csharp
public record TaskDetailsData
{
    public required int Id { get; init; }
    public required SubscriptionSummary Subscription { get; init; }
    public required RecipientSummary Recipient { get; init; }
    public required int ResultCount { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required bool Resolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedByUserId { get; init; }
    public string? ResolvedByUserName { get; init; }
    public string? ResolutionNotes { get; init; }
    public QueryExecutionSummary? ExecutionDetails { get; init; }
    public string? StoredResults { get; init; } // JSON from Notification.Results
}

public record SubscriptionSummary(int Id, string Name, string? Description);
public record RecipientSummary(int Id, string Name, NotificationType Type);
public record QueryExecutionSummary(int Id, DateTime ExecutedAt, double ExecutionTimeMs, NotificationStatus Status);
```

**Mapping**: Requires joins to Subscription, Recipient, Notification, QueryExecutionHistory

---

### TaskStatistics (Dashboard)

**Purpose**: Aggregate statistics for Tasks page header.

**Location**: `Semantico.Core/DTOs/TaskStatisticsData.cs`

```csharp
public record TaskStatisticsData
{
    public required int TotalTasks { get; init; }
    public required int UnresolvedCount { get; init; }
    public required int ResolvedCount { get; init; }
    public double? AverageResolutionTimeHours { get; init; }
}
```

**Calculation**: Aggregations on Task table:
- `TotalTasks`: `COUNT(*) WHERE ArchivedTime IS NULL`
- `UnresolvedCount`: `COUNT(*) WHERE Resolved = false AND ArchivedTime IS NULL`
- `ResolvedCount`: `COUNT(*) WHERE Resolved = true AND ArchivedTime IS NULL`
- `AverageResolutionTimeHours`: `AVG(DATEDIFF(hour, CreatedTime, ResolvedAt)) WHERE Resolved = true`

---

## Query Patterns

### Common Queries

#### 1. Get All Unresolved Tasks for User
```csharp
// Uses IX_Task_RecipientId_Resolved_CreatedTime index
var unresolvedTasks = await context.Tasks
    .Where(t => t.ArchivedTime == null && t.Resolved == false)
    .OrderByDescending(t => t.CreatedTime)
    .Include(t => t.Subscription)
    .Include(t => t.Recipient)
    .Include(t => t.Notification)
    .ToListAsync();
```

#### 2. Get Task Timeline for Subscription
```csharp
// Uses IX_Task_SubscriptionId_CreatedTime index
var timeline = await context.Tasks
    .Where(t => t.SubscriptionId == subscriptionId && t.ArchivedTime == null)
    .OrderByDescending(t => t.CreatedTime)
    .Select(t => new { t.CreatedTime, t.ResultCount, t.Resolved })
    .ToListAsync();
```

#### 3. Get Task Details
```csharp
// Uses IX_Task_NotificationId unique index for join
var details = await context.Tasks
    .Where(t => t.Id == taskId)
    .Include(t => t.Subscription)
    .Include(t => t.Recipient)
    .Include(t => t.Notification)
        .ThenInclude(n => n.QueryExecutionHistory)
    .FirstOrDefaultAsync();
```

---

## Validation Summary

### Entity Validation (Data Annotations)

- ✅ Required fields enforce non-null constraints
- ✅ `ResolutionNotes` max length prevents over-sized text storage
- ✅ Navigation properties marked with `null!` (required but set by EF Core)

### Business Rule Validation (Handler Layer)

- ✅ **ResolveTask**: Verify task exists, update resolution fields atomically
- ✅ **ReopenTask**: Verify task is resolved before reopening
- ✅ **CreateTask**: Verify NotificationId references Tasks recipient notification

### Database Constraint Validation

- ✅ Foreign key constraints prevent orphaned tasks
- ✅ Unique index on NotificationId prevents duplicate tasks per notification
- ✅ Restrict delete behavior protects historical data integrity

---

## Migration Checklist

Before committing migrations, verify:

- [ ] No hardcoded schema references (e.g., `"semantico"."Tasks"` → `"Tasks"`)
- [ ] Both PostgreSQL and SQL Server migrations generated
- [ ] Migrations tested on both providers with different schema names
- [ ] Indexes created with explicit names (avoid auto-generated names)
- [ ] Foreign key constraints configured with Restrict delete behavior
- [ ] `ResolutionNotes` max length applied in migration
- [ ] DbSet<Task> added to SemanticoContext

---

## Future Enhancements (Out of Scope)

- **Task Assignment**: Add `AssignedToUserId` field (P4 user story)
- **Task Priority**: Add `Priority` enum field (deferred per spec)
- **Resolution History**: Add separate `TaskResolution` entity for audit trail
- **Task Due Dates**: Add `DueAt` nullable DateTime field
- **Concurrency Control**: Add `[Timestamp]` property for optimistic concurrency

---

## Summary

Data model designed for:
- ✅ Clean Architecture (entity in Core, no infrastructure dependencies)
- ✅ Multi-provider compatibility (no PostgreSQL/SQL Server-specific types)
- ✅ Schema-agnostic migrations (runtime schema selection)
- ✅ Query performance (4 strategic indexes)
- ✅ Data integrity (foreign key constraints, validation rules)
- ✅ Future extensibility (BaseArchivableEntity, nullable fields for future features)

Ready to proceed to contracts generation (Phase 1 continuation).
