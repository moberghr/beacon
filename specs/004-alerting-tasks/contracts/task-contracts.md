# CQRS Contracts: Alerting Tasks Recipient

**Feature**: 004-alerting-tasks | **Date**: 2025-11-12

## Overview

This document defines the MediatR handler contracts (request/response types) for all task operations following the CQRS pattern. Each handler is defined as `internal sealed class` with primary constructor injection, implementing `IRequestHandler<TRequest, TResponse>`.

All handlers reside in `Semantico.Core/Features/Tasks/` directory.

---

## Command Handlers (Write Operations)

### CreateTask

**Purpose**: Create a new task record when TasksAdapter sends a notification.

**File**: `Semantico.Core/Features/Tasks/CreateTask.cs`

**Handler Signature**:
```csharp
internal sealed class Handler(SemanticoContext context) : IRequestHandler<CreateTaskRequest, CreateTaskResponse>
```

**Request Contract**:
```csharp
public sealed record CreateTaskRequest : IRequest<CreateTaskResponse>
{
    public required int NotificationId { get; init; }
    public required int SubscriptionId { get; init; }
    public required int RecipientId { get; init; }
    public required int ResultCount { get; init; }
}
```

**Response Contract**:
```csharp
public sealed record CreateTaskResponse
{
    public required int TaskId { get; init; }
    public required DateTime CreatedAt { get; init; }
}
```

**Validation Rules**:
- NotificationId must reference existing Notification record
- SubscriptionId must reference existing Subscription record
- RecipientId must reference existing Recipient with NotificationType = Tasks
- ResultCount must be >= 0

**Error Handling**:
- Throws `SemanticoException` if notification/subscription/recipient not found
- Throws `SemanticoException` if recipient is not Tasks type
- EF Core foreign key constraint violation if references invalid

**Example Usage** (from TasksAdapter):
```csharp
var response = await _mediator.Send(new CreateTaskRequest
{
    NotificationId = notificationId,
    SubscriptionId = subscriptionId,
    RecipientId = recipientId,
    ResultCount = queryResult.TotalRecords
}, cancellationToken);
```

---

### ResolveTask

**Purpose**: Mark a task as resolved with optional notes.

**File**: `Semantico.Core/Features/Tasks/ResolveTask.cs`

**Handler Signature**:
```csharp
internal sealed class Handler(SemanticoContext context) : IRequestHandler<ResolveTaskRequest, ResolveTaskResponse>
```

**Request Contract**:
```csharp
public sealed record ResolveTaskRequest : IRequest<ResolveTaskResponse>
{
    public required int TaskId { get; init; }
    public string? ResolutionNotes { get; init; }
    public string? ResolvedByUserId { get; init; } // Optional: populated when auth integrated
}
```

**Response Contract**:
```csharp
public sealed record ResolveTaskResponse
{
    public required int TaskId { get; init; }
    public required DateTime ResolvedAt { get; init; }
    public required bool Success { get; init; }
}
```

**Validation Rules**:
- TaskId must reference existing task
- ResolutionNotes max length 2000 characters
- Task can be resolved even if already resolved (updates resolution fields)

**Business Logic**:
- Set `Resolved = true`
- Set `ResolvedAt = DateTime.UtcNow`
- Set `ResolutionNotes` if provided
- Set `ResolvedByUserId` if provided

**Error Handling**:
- Throws `SemanticoException` if task not found
- Throws `ArgumentException` if ResolutionNotes exceeds 2000 characters

**Example Usage** (from TaskService):
```csharp
var response = await _mediator.Send(new ResolveTaskRequest
{
    TaskId = taskId,
    ResolutionNotes = "Data quality issue fixed by updating source system",
    ResolvedByUserId = currentUserId
}, cancellationToken);
```

---

### ReopenTask

**Purpose**: Reopen a resolved task (set Resolved = false).

**File**: `Semantico.Core/Features/Tasks/ReopenTask.cs`

**Handler Signature**:
```csharp
internal sealed class Handler(SemanticoContext context) : IRequestHandler<ReopenTaskRequest, ReopenTaskResponse>
```

**Request Contract**:
```csharp
public sealed record ReopenTaskRequest : IRequest<ReopenTaskResponse>
{
    public required int TaskId { get; init; }
}
```

**Response Contract**:
```csharp
public sealed record ReopenTaskResponse
{
    public required int TaskId { get; init; }
    public required bool Success { get; init; }
}
```

**Validation Rules**:
- TaskId must reference existing task
- Task must be currently resolved (Resolved = true)

**Business Logic**:
- Set `Resolved = false`
- Set `ResolvedAt = null`
- Clear `ResolutionNotes` (or append "Reopened" note - design decision)

**Error Handling**:
- Throws `SemanticoException` if task not found
- Throws `SemanticoException` if task already unresolved (idempotent operation)

**Example Usage** (from TaskService):
```csharp
var response = await _mediator.Send(new ReopenTaskRequest
{
    TaskId = taskId
}, cancellationToken);
```

---

## Query Handlers (Read Operations)

### GetTasks

**Purpose**: Retrieve task list with filtering, sorting, and pagination.

**File**: `Semantico.Core/Features/Tasks/GetTasks.cs`

**Handler Signature**:
```csharp
internal sealed class Handler(SemanticoContext context) : IRequestHandler<GetTasksRequest, GetTasksResponse>
```

**Request Contract**:
```csharp
public sealed record GetTasksRequest : IRequest<GetTasksResponse>
{
    public int? RecipientId { get; init; }
    public int? SubscriptionId { get; init; }
    public bool? Resolved { get; init; } // null = all, true = resolved only, false = unresolved only
    public DateTime? CreatedAfter { get; init; }
    public DateTime? CreatedBefore { get; init; }
    public int? Limit { get; init; }
    public int? Offset { get; init; }
    public TaskSortField? SortBy { get; init; } = TaskSortField.CreatedTime;
    public SortDirection? SortDirection { get; init; } = SortDirection.Descending;
}

public enum TaskSortField
{
    CreatedTime,
    ResolvedAt,
    ResultCount,
    SubscriptionName
}

public enum SortDirection
{
    Ascending,
    Descending
}
```

**Response Contract**:
```csharp
public sealed record GetTasksResponse
{
    public required List<TaskData> Tasks { get; init; }
    public required int TotalCount { get; init; }
}

// TaskData defined in data-model.md:
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

**Validation Rules**:
- Limit max 1000 (prevent excessive data loading)
- Offset >= 0
- If RecipientId specified, must reference existing recipient

**Query Optimization**:
- Uses `IX_Task_RecipientId_Resolved_CreatedTime` index when filtering by recipient + resolved
- Uses `IX_Task_SubscriptionId_CreatedTime` index when filtering by subscription
- Includes joins to Subscription, Query, Recipient for display names

**Error Handling**:
- Returns empty list if no tasks match filters (no exception)
- Throws `ArgumentException` if Limit > 1000

**Example Usage** (from TaskService):
```csharp
// Get all unresolved tasks
var response = await _mediator.Send(new GetTasksRequest
{
    Resolved = false,
    Limit = 100
}, cancellationToken);

// Get tasks for specific subscription
var response = await _mediator.Send(new GetTasksRequest
{
    SubscriptionId = 42,
    SortBy = TaskSortField.CreatedTime,
    SortDirection = SortDirection.Descending
}, cancellationToken);
```

---

### GetTaskDetails

**Purpose**: Retrieve full task details including execution history and stored results.

**File**: `Semantico.Core/Features/Tasks/GetTaskDetails.cs`

**Handler Signature**:
```csharp
internal sealed class Handler(SemanticoContext context) : IRequestHandler<GetTaskDetailsRequest, GetTaskDetailsResponse>
```

**Request Contract**:
```csharp
public sealed record GetTaskDetailsRequest : IRequest<GetTaskDetailsResponse>
{
    public required int TaskId { get; init; }
}
```

**Response Contract**:
```csharp
public sealed record GetTaskDetailsResponse
{
    public required TaskDetailsData? TaskDetails { get; init; } // null if not found
}

// TaskDetailsData defined in data-model.md:
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

**Validation Rules**:
- TaskId must be positive integer
- Returns null if task not found (no exception)

**Query Optimization**:
- Uses `IX_Task_NotificationId` unique index for joins
- Eagerly loads Subscription, Recipient, Notification, QueryExecutionHistory
- Only loads Notification.Results if StoreResults was enabled on subscription

**Error Handling**:
- Returns `TaskDetails = null` if task not found
- No exception thrown for not found (UI displays "Task not found" message)

**Example Usage** (from TaskService):
```csharp
var response = await _mediator.Send(new GetTaskDetailsRequest
{
    TaskId = 42
}, cancellationToken);

if (response.TaskDetails is null)
{
    throw new SemanticoException("Task not found");
}
```

---

### GetTaskStatistics

**Purpose**: Calculate aggregate statistics for tasks dashboard.

**File**: `Semantico.Core/Features/Tasks/GetTaskStatistics.cs`

**Handler Signature**:
```csharp
internal sealed class Handler(SemanticoContext context) : IRequestHandler<GetTaskStatisticsRequest, GetTaskStatisticsResponse>
```

**Request Contract**:
```csharp
public sealed record GetTaskStatisticsRequest : IRequest<GetTaskStatisticsResponse>
{
    public int? RecipientId { get; init; } // Optional: filter statistics by recipient
}
```

**Response Contract**:
```csharp
public sealed record GetTaskStatisticsResponse
{
    public required TaskStatisticsData Statistics { get; init; }
}

// TaskStatisticsData defined in data-model.md:
public record TaskStatisticsData
{
    public required int TotalTasks { get; init; }
    public required int UnresolvedCount { get; init; }
    public required int ResolvedCount { get; init; }
    public double? AverageResolutionTimeHours { get; init; }
}
```

**Validation Rules**:
- RecipientId must reference existing recipient if provided

**Query Optimization**:
- Single aggregation query (no N+1 problem)
- Uses `IX_Task_RecipientId_Resolved_CreatedTime` index if RecipientId filter applied
- Calculates averages using SQL `AVG()` function

**Calculation Logic**:
```sql
-- TotalTasks
SELECT COUNT(*) FROM Tasks WHERE ArchivedTime IS NULL [AND RecipientId = @RecipientId]

-- UnresolvedCount
SELECT COUNT(*) FROM Tasks WHERE Resolved = 0 AND ArchivedTime IS NULL [AND RecipientId = @RecipientId]

-- ResolvedCount
SELECT COUNT(*) FROM Tasks WHERE Resolved = 1 AND ArchivedTime IS NULL [AND RecipientId = @RecipientId]

-- AverageResolutionTimeHours
SELECT AVG(DATEDIFF(hour, CreatedTime, ResolvedAt))
FROM Tasks
WHERE Resolved = 1 AND ResolvedAt IS NOT NULL [AND RecipientId = @RecipientId]
```

**Error Handling**:
- Returns zero counts if no tasks exist (no exception)
- Returns null for `AverageResolutionTimeHours` if no resolved tasks

**Example Usage** (from TaskService):
```csharp
// Global statistics
var response = await _mediator.Send(new GetTaskStatisticsRequest(), cancellationToken);

// Statistics for specific recipient
var response = await _mediator.Send(new GetTaskStatisticsRequest
{
    RecipientId = 5
}, cancellationToken);
```

---

## Service Layer Interface

### ITaskService

**Purpose**: Abstraction layer between Blazor UI and MediatR handlers.

**Location**: `Semantico.Core/Services/ITaskService.cs`

**Interface Definition**:
```csharp
public interface ITaskService
{
    Task<CreateTaskResponse> CreateTask(CreateTaskRequest request, CancellationToken cancellationToken);
    Task<ResolveTaskResponse> ResolveTask(int taskId, string? resolutionNotes, string? userId, CancellationToken cancellationToken);
    Task<ReopenTaskResponse> ReopenTask(int taskId, CancellationToken cancellationToken);
    Task<List<TaskData>> GetTasks(int? recipientId, int? subscriptionId, bool? resolved, CancellationToken cancellationToken);
    Task<TaskDetailsData?> GetTaskDetails(int taskId, CancellationToken cancellationToken);
    Task<TaskStatisticsData> GetTaskStatistics(int? recipientId, CancellationToken cancellationToken);
}
```

**Implementation**: `Semantico.Core/Services/TaskService.cs`

**Pattern**: Each method wraps a MediatR `Send()` call:
```csharp
public class TaskService(IMediator mediator) : ITaskService
{
    private readonly IMediator _mediator = mediator;

    public async Task<ResolveTaskResponse> ResolveTask(int taskId, string? resolutionNotes, string? userId, CancellationToken cancellationToken)
    {
        return await _mediator.Send(new ResolveTaskRequest
        {
            TaskId = taskId,
            ResolutionNotes = resolutionNotes,
            ResolvedByUserId = userId
        }, cancellationToken);
    }

    // ... other methods follow same pattern
}
```

**DI Registration**: Add to `Program.cs` or service configuration:
```csharp
services.AddScoped<ITaskService, TaskService>();
```

---

## Handler Implementation Guidelines

### Standard Handler Structure

All handlers follow this template:

```csharp
namespace Semantico.Core.Features.Tasks;

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Semantico.Core.Data;

internal sealed class [Operation]
{
    internal sealed class Handler(SemanticoContext context) : IRequestHandler<Request, Response>
    {
        private readonly SemanticoContext _context = context;

        public async Task<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            // 1. Validation
            // 2. Business logic
            // 3. Database operation
            // 4. Return response
        }
    }
}

// Request at end of file
public sealed record Request : IRequest<Response>
{
    // Properties
}

// Response at end of file
public sealed record Response
{
    // Properties
}
```

### Error Handling Pattern

- **Not Found**: Throw `SemanticoException` with descriptive message
- **Validation Failed**: Throw `ArgumentException` or `SemanticoException`
- **Database Errors**: Let EF Core exceptions bubble up (handled by middleware)
- **Idempotent Operations**: No error for repeated operations (e.g., resolving resolved task)

### Testing Strategy

- **Unit Tests**: Mock `SemanticoContext` using in-memory database or mocked DbSet
- **Integration Tests**: Test handlers against real PostgreSQL and SQL Server instances
- **Test Coverage**: All validation rules, business logic branches, error cases

---

## Contract Versioning

All contracts are version 1.0 (initial implementation). Future changes:

- **Breaking Changes**: Create new request/response types (e.g., `ResolveTaskRequestV2`)
- **Non-Breaking Changes**: Add optional properties with defaults
- **Deprecation**: Mark old contracts with `[Obsolete]` attribute

---

## Summary

CQRS contracts defined for:
- ✅ 3 command handlers (CreateTask, ResolveTask, ReopenTask)
- ✅ 3 query handlers (GetTasks, GetTaskDetails, GetTaskStatistics)
- ✅ Service layer interface (ITaskService) for UI abstraction
- ✅ Strong typing with sealed records (immutable DTOs)
- ✅ Primary constructor injection (constitutional compliance)
- ✅ Clear validation rules and error handling patterns

Ready to proceed to quickstart.md (Phase 1 continuation).
