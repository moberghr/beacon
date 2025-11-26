# Quick Start: Alerting Tasks Recipient Implementation

**Feature**: 004-alerting-tasks | **Date**: 2025-11-12

## Overview

This guide provides a step-by-step walkthrough for implementing the Tasks recipient feature in Semantico. Follow these steps in order to ensure clean integration with existing architecture and constitutional compliance.

**Estimated Implementation Time**: 8-12 hours (P1 + P2 user stories)

---

## Prerequisites

- [ ] Semantico development environment set up
- [ ] .NET 8.0 SDK installed
- [ ] PostgreSQL and SQL Server test instances available
- [ ] Feature branch `004-alerting-tasks` checked out
- [ ] Spec, plan, data-model, and contracts documentation reviewed

---

## Implementation Phases

### Phase 1: Core Data Layer (2-3 hours)

#### Step 1.1: Add Tasks to NotificationType Enum

**File**: `Semantico.Core/Data/Enums/NotificationType.cs`

**Action**: Add Tasks enum value

```csharp
public enum NotificationType
{
    Teams = 1,
    Email = 2,
    Jira = 3,
    Tasks = 4  // ADD THIS LINE
}
```

**Verification**: Build solution to ensure no compilation errors

```bash
dotnet build --property WarningLevel=0
```

---

#### Step 1.2: Create Task Entity

**File**: `Semantico.Core/Data/Entities/Task.cs`

**Action**: Create Task entity inheriting from BaseArchivableEntity

```csharp
namespace Semantico.Core.Data.Entities;

public class Task : BaseArchivableEntity
{
    public required int NotificationId { get; set; }
    public required int SubscriptionId { get; set; }
    public required int RecipientId { get; set; }
    public required int ResultCount { get; set; }
    public bool Resolved { get; set; } = false;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByUserId { get; set; }
    public string? ResolutionNotes { get; set; }

    // Navigation properties
    public Notification Notification { get; set; } = null!;
    public Subscription Subscription { get; set; } = null!;
    public Recipient Recipient { get; set; } = null!;
}
```

**Verification**: Entity compiles without errors

---

#### Step 1.3: Configure Task Entity in SemanticoContext

**File**: `Semantico.Core/Data/SemanticoContext.cs`

**Action 1**: Add DbSet property

```csharp
public DbSet<Task> Tasks => Set<Task>();
```

**Action 2**: Add entity configuration in `OnModelCreating` method

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // ... existing configurations ...

    // Task entity configuration
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
}
```

**Verification**: Build solution and verify no errors

---

#### Step 1.4: Create DTOs

**File 1**: `Semantico.Core/DTOs/TaskData.cs`

```csharp
namespace Semantico.Core.DTOs;

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

**File 2**: `Semantico.Core/DTOs/TaskDetailsData.cs`

```csharp
namespace Semantico.Core.DTOs;

using Semantico.Core.Data.Enums;

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
    public string? StoredResults { get; init; }
}

public record SubscriptionSummary(int Id, string Name, string? Description);
public record RecipientSummary(int Id, string Name, NotificationType Type);
public record QueryExecutionSummary(int Id, DateTime ExecutedAt, double ExecutionTimeMs, NotificationStatus Status);
```

**File 3**: `Semantico.Core/DTOs/TaskStatisticsData.cs`

```csharp
namespace Semantico.Core.DTOs;

public record TaskStatisticsData
{
    public required int TotalTasks { get; init; }
    public required int UnresolvedCount { get; init; }
    public required int ResolvedCount { get; init; }
    public double? AverageResolutionTimeHours { get; init; }
}
```

**Verification**: Build solution

---

#### Step 1.5: Generate Database Migrations

**⚠️ IMPORTANT**: User must generate migrations manually per CLAUDE.md guidance.

**PostgreSQL Migration**:
```bash
dotnet ef migrations add AddTaskEntity --project Semantico.Core.PostgreSql --startup-project Semantico.SampleProject
```

**SQL Server Migration**:
```bash
dotnet ef migrations add AddTaskEntity --project Semantico.Core.SqlServer --startup-project Semantico.SampleProject
```

**Migration Validation Checklist**:
- [ ] No hardcoded `"semantico"."Tasks"` references (should be just `"Tasks"`)
- [ ] Indexes created with explicit names (IX_Task_*)
- [ ] Foreign key constraints configured with Restrict delete behavior
- [ ] ResolutionNotes max length 2000 applied
- [ ] Test migration on both PostgreSQL and SQL Server with different schema names

**Apply Migrations**:
```bash
dotnet ef database update --project Semantico.Core --startup-project Semantico.SampleProject
```

---

### Phase 2: CQRS Handlers (3-4 hours)

#### Step 2.1: Create Handler Directory

```bash
mkdir -p Semantico.Core/Features/Tasks
```

---

#### Step 2.2: Implement CreateTask Handler

**File**: `Semantico.Core/Features/Tasks/CreateTask.cs`

```csharp
namespace Semantico.Core.Features.Tasks;

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Exceptions;

internal sealed class CreateTask
{
    internal sealed class Handler(SemanticoContext context) : IRequestHandler<Request, Response>
    {
        private readonly SemanticoContext _context = context;

        public async Task<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            // Validate recipient is Tasks type
            var recipient = await _context.Recipients
                .FindAsync(new object[] { request.RecipientId }, cancellationToken)
                ?? throw new SemanticoException($"Recipient {request.RecipientId} not found");

            if (recipient.NotificationType != NotificationType.Tasks)
            {
                throw new SemanticoException($"Recipient {request.RecipientId} is not a Tasks recipient");
            }

            // Create task
            var task = new Data.Entities.Task
            {
                NotificationId = request.NotificationId,
                SubscriptionId = request.SubscriptionId,
                RecipientId = request.RecipientId,
                ResultCount = request.ResultCount,
                Resolved = false
            };

            _context.Tasks.Add(task);
            await _context.SaveChangesAsync(cancellationToken);

            return new Response
            {
                TaskId = task.Id,
                CreatedAt = task.CreatedTime
            };
        }
    }
}

public sealed record Request : IRequest<Response>
{
    public required int NotificationId { get; init; }
    public required int SubscriptionId { get; init; }
    public required int RecipientId { get; init; }
    public required int ResultCount { get; init; }
}

public sealed record Response
{
    public required int TaskId { get; init; }
    public required DateTime CreatedAt { get; init; }
}
```

---

#### Step 2.3: Implement ResolveTask Handler

**File**: `Semantico.Core/Features/Tasks/ResolveTask.cs`

```csharp
namespace Semantico.Core.Features.Tasks;

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Semantico.Core.Data;
using Semantico.Core.Exceptions;

internal sealed class ResolveTask
{
    internal sealed class Handler(SemanticoContext context) : IRequestHandler<Request, Response>
    {
        private readonly SemanticoContext _context = context;

        public async Task<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            var task = await _context.Tasks
                .FindAsync(new object[] { request.TaskId }, cancellationToken)
                ?? throw new SemanticoException($"Task {request.TaskId} not found");

            // Validate resolution notes length
            if (request.ResolutionNotes?.Length > 2000)
            {
                throw new ArgumentException("Resolution notes cannot exceed 2000 characters");
            }

            // Update task resolution fields
            task.Resolved = true;
            task.ResolvedAt = DateTime.UtcNow;
            task.ResolutionNotes = request.ResolutionNotes;
            task.ResolvedByUserId = request.ResolvedByUserId;

            await _context.SaveChangesAsync(cancellationToken);

            return new Response
            {
                TaskId = task.Id,
                ResolvedAt = task.ResolvedAt.Value,
                Success = true
            };
        }
    }
}

public sealed record Request : IRequest<Response>
{
    public required int TaskId { get; init; }
    public string? ResolutionNotes { get; init; }
    public string? ResolvedByUserId { get; init; }
}

public sealed record Response
{
    public required int TaskId { get; init; }
    public required DateTime ResolvedAt { get; init; }
    public required bool Success { get; init; }
}
```

---

#### Step 2.4: Implement ReopenTask Handler

**File**: `Semantico.Core/Features/Tasks/ReopenTask.cs`

```csharp
namespace Semantico.Core.Features.Tasks;

using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Semantico.Core.Data;
using Semantico.Core.Exceptions;

internal sealed class ReopenTask
{
    internal sealed class Handler(SemanticoContext context) : IRequestHandler<Request, Response>
    {
        private readonly SemanticoContext _context = context;

        public async Task<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            var task = await _context.Tasks
                .FindAsync(new object[] { request.TaskId }, cancellationToken)
                ?? throw new SemanticoException($"Task {request.TaskId} not found");

            if (!task.Resolved)
            {
                // Idempotent: already unresolved
                return new Response { TaskId = task.Id, Success = true };
            }

            // Reopen task
            task.Resolved = false;
            task.ResolvedAt = null;
            task.ResolutionNotes = null;

            await _context.SaveChangesAsync(cancellationToken);

            return new Response { TaskId = task.Id, Success = true };
        }
    }
}

public sealed record Request : IRequest<Response>
{
    public required int TaskId { get; init; }
}

public sealed record Response
{
    public required int TaskId { get; init; }
    public required bool Success { get; init; }
}
```

---

#### Step 2.5: Implement GetTasks Query Handler

**File**: `Semantico.Core/Features/Tasks/GetTasks.cs`

```csharp
namespace Semantico.Core.Features.Tasks;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.DTOs;

internal sealed class GetTasks
{
    internal sealed class Handler(SemanticoContext context) : IRequestHandler<Request, Response>
    {
        private readonly SemanticoContext _context = context;

        public async Task<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            var query = _context.Tasks
                .Where(t => t.ArchivedTime == null);

            // Apply filters
            if (request.RecipientId.HasValue)
                query = query.Where(t => t.RecipientId == request.RecipientId.Value);

            if (request.SubscriptionId.HasValue)
                query = query.Where(t => t.SubscriptionId == request.SubscriptionId.Value);

            if (request.Resolved.HasValue)
                query = query.Where(t => t.Resolved == request.Resolved.Value);

            // Count total before pagination
            var totalCount = await query.CountAsync(cancellationToken);

            // Apply sorting
            query = query.OrderByDescending(t => t.CreatedTime);

            // Apply pagination
            if (request.Offset.HasValue)
                query = query.Skip(request.Offset.Value);

            if (request.Limit.HasValue)
                query = query.Take(Math.Min(request.Limit.Value, 1000));

            // Project to DTO with joins
            var tasks = await query
                .Include(t => t.Subscription)
                    .ThenInclude(s => s.Query)
                .Include(t => t.Recipient)
                .Select(t => new TaskData
                {
                    Id = t.Id,
                    SubscriptionName = t.Subscription.Name,
                    QueryName = t.Subscription.Query.Name,
                    RecipientName = t.Recipient.Name,
                    ResultCount = t.ResultCount,
                    CreatedAt = t.CreatedTime,
                    Resolved = t.Resolved,
                    ResolvedAt = t.ResolvedAt,
                    ResolvedByUserName = null // TODO: lookup from user service when auth integrated
                })
                .ToListAsync(cancellationToken);

            return new Response
            {
                Tasks = tasks,
                TotalCount = totalCount
            };
        }
    }
}

public sealed record Request : IRequest<Response>
{
    public int? RecipientId { get; init; }
    public int? SubscriptionId { get; init; }
    public bool? Resolved { get; init; }
    public int? Limit { get; init; }
    public int? Offset { get; init; }
}

public sealed record Response
{
    public required List<TaskData> Tasks { get; init; }
    public required int TotalCount { get; init; }
}
```

---

#### Step 2.6: Implement GetTaskDetails Query Handler

**File**: `Semantico.Core/Features/Tasks/GetTaskDetails.cs`

```csharp
namespace Semantico.Core.Features.Tasks;

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.DTOs;

internal sealed class GetTaskDetails
{
    internal sealed class Handler(SemanticoContext context) : IRequestHandler<Request, Response>
    {
        private readonly SemanticoContext _context = context;

        public async Task<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            var task = await _context.Tasks
                .Where(t => t.Id == request.TaskId)
                .Include(t => t.Subscription)
                .Include(t => t.Recipient)
                .Include(t => t.Notification)
                    .ThenInclude(n => n.QueryExecutionHistory)
                .FirstOrDefaultAsync(cancellationToken);

            if (task is null)
            {
                return new Response { TaskDetails = null };
            }

            var details = new TaskDetailsData
            {
                Id = task.Id,
                Subscription = new SubscriptionSummary(
                    task.Subscription.Id,
                    task.Subscription.Name,
                    task.Subscription.Description
                ),
                Recipient = new RecipientSummary(
                    task.Recipient.Id,
                    task.Recipient.Name,
                    task.Recipient.NotificationType
                ),
                ResultCount = task.ResultCount,
                CreatedAt = task.CreatedTime,
                Resolved = task.Resolved,
                ResolvedAt = task.ResolvedAt,
                ResolvedByUserId = task.ResolvedByUserId,
                ResolvedByUserName = null, // TODO: lookup when auth integrated
                ResolutionNotes = task.ResolutionNotes,
                ExecutionDetails = task.Notification.QueryExecutionHistory != null
                    ? new QueryExecutionSummary(
                        task.Notification.QueryExecutionHistory.Id,
                        task.Notification.QueryExecutionHistory.ExecutedAt,
                        task.Notification.QueryExecutionHistory.ExecutionTime,
                        task.Notification.QueryExecutionHistory.NotificationStatus
                    )
                    : null,
                StoredResults = task.Notification.Results
            };

            return new Response { TaskDetails = details };
        }
    }
}

public sealed record Request : IRequest<Response>
{
    public required int TaskId { get; init; }
}

public sealed record Response
{
    public required TaskDetailsData? TaskDetails { get; init; }
}
```

**Verification**: Build all handlers and verify no compilation errors

---

### Phase 3: TasksAdapter (1-2 hours)

#### Step 3.1: Implement TasksAdapter

**File**: `Semantico.Core/Adapters/TasksAdapter.cs`

```csharp
namespace Semantico.Core.Adapters;

using System.Threading.Tasks;
using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Features.Tasks;

public class TasksAdapter(IMediator mediator) : IAdapter
{
    private readonly IMediator _mediator = mediator;

    public NotificationType NotificationType => NotificationType.Tasks;

    public async Task SendNotificationAsync(
        RecipientQueryResult recipientQueryResult,
        int? lastNotificationResultCount)
    {
        // Extract required data
        var notificationId = recipientQueryResult.NotificationId
            ?? throw new ArgumentException("NotificationId is required for Tasks adapter");

        var subscriptionId = recipientQueryResult.QueryResult.SubscriptionId
            ?? throw new ArgumentException("SubscriptionId is required for Tasks adapter");

        // Create task via MediatR handler
        await _mediator.Send(new CreateTask.Request
        {
            NotificationId = notificationId.Value,
            SubscriptionId = subscriptionId.Value,
            RecipientId = GetRecipientId(recipientQueryResult),
            ResultCount = recipientQueryResult.QueryResult.TotalRecords
        });
    }

    private static int GetRecipientId(RecipientQueryResult result)
    {
        // Extract recipient ID from RecipientData list
        var recipient = result.QueryResult.Recipients
            .FirstOrDefault(r => r.NotificationType == NotificationType.Tasks)
            ?? throw new ArgumentException("No Tasks recipient found in query result");

        return recipient.Id;
    }
}
```

---

#### Step 3.2: Register TasksAdapter in AdapterFactory

**File**: `Semantico.Core/Adapters/AdapterFactory.cs`

**Action**: Add Tasks case to `GetAdapterService` method

```csharp
public IAdapter GetAdapterService(NotificationType notificationType)
{
    return notificationType switch
    {
        NotificationType.Email => new EmailAdapter(_serviceProvider),
        NotificationType.Teams => new TeamsAdapter(_serviceProvider),
        NotificationType.Jira => new JiraAdapter(_serviceProvider),
        NotificationType.Tasks => new TasksAdapter(_serviceProvider.GetRequiredService<IMediator>()), // ADD THIS LINE
        _ => throw new ArgumentException($"Unsupported notification type: {notificationType}")
    };
}
```

**Verification**: Build solution and verify adapter registration compiles

---

### Phase 4: Service Layer (1 hour)

#### Step 4.1: Create ITaskService Interface

**File**: `Semantico.Core/Services/ITaskService.cs`

```csharp
namespace Semantico.Core.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Semantico.Core.DTOs;

public interface ITaskService
{
    Task<int> CreateTask(int notificationId, int subscriptionId, int recipientId, int resultCount, CancellationToken cancellationToken);
    Task ResolveTask(int taskId, string? resolutionNotes, string? userId, CancellationToken cancellationToken);
    Task ReopenTask(int taskId, CancellationToken cancellationToken);
    Task<List<TaskData>> GetTasks(int? recipientId, int? subscriptionId, bool? resolved, CancellationToken cancellationToken);
    Task<TaskDetailsData?> GetTaskDetails(int taskId, CancellationToken cancellationToken);
    Task<TaskStatisticsData> GetTaskStatistics(int? recipientId, CancellationToken cancellationToken);
}
```

---

#### Step 4.2: Implement TaskService

**File**: `Semantico.Core/Services/TaskService.cs`

```csharp
namespace Semantico.Core.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Semantico.Core.DTOs;
using Semantico.Core.Features.Tasks;

public class TaskService(IMediator mediator) : ITaskService
{
    private readonly IMediator _mediator = mediator;

    public async Task<int> CreateTask(int notificationId, int subscriptionId, int recipientId, int resultCount, CancellationToken cancellationToken)
    {
        var response = await _mediator.Send(new CreateTask.Request
        {
            NotificationId = notificationId,
            SubscriptionId = subscriptionId,
            RecipientId = recipientId,
            ResultCount = resultCount
        }, cancellationToken);

        return response.TaskId;
    }

    public async Task ResolveTask(int taskId, string? resolutionNotes, string? userId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new ResolveTask.Request
        {
            TaskId = taskId,
            ResolutionNotes = resolutionNotes,
            ResolvedByUserId = userId
        }, cancellationToken);
    }

    public async Task ReopenTask(int taskId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new ReopenTask.Request
        {
            TaskId = taskId
        }, cancellationToken);
    }

    public async Task<List<TaskData>> GetTasks(int? recipientId, int? subscriptionId, bool? resolved, CancellationToken cancellationToken)
    {
        var response = await _mediator.Send(new GetTasks.Request
        {
            RecipientId = recipientId,
            SubscriptionId = subscriptionId,
            Resolved = resolved
        }, cancellationToken);

        return response.Tasks;
    }

    public async Task<TaskDetailsData?> GetTaskDetails(int taskId, CancellationToken cancellationToken)
    {
        var response = await _mediator.Send(new GetTaskDetails.Request
        {
            TaskId = taskId
        }, cancellationToken);

        return response.TaskDetails;
    }

    public async Task<TaskStatisticsData> GetTaskStatistics(int? recipientId, CancellationToken cancellationToken)
    {
        // TODO: Implement GetTaskStatistics handler
        return new TaskStatisticsData
        {
            TotalTasks = 0,
            UnresolvedCount = 0,
            ResolvedCount = 0,
            AverageResolutionTimeHours = null
        };
    }
}
```

---

#### Step 4.3: Register TaskService in DI

**File**: `Semantico.SampleProject/Program.cs` (or wherever services are registered)

```csharp
services.AddScoped<ITaskService, TaskService>();
```

**Verification**: Build and run application to verify service registration

---

### Phase 5: Blazor UI Components (3-4 hours)

#### Step 5.1: Create Tasks Page

**File**: `Semantico.UI/Components/Pages/Tasks/Tasks.razor`

```razor
@page "/tasks"
@using Semantico.Core.Services
@using Semantico.Core.DTOs
@inject ITaskService TaskService
@inject NavigationManager Navigation

<h3>Tasks</h3>

<div class="filters">
    <label>
        Status:
        <select @bind="resolvedFilter">
            <option value="">All</option>
            <option value="false">Unresolved</option>
            <option value="true">Resolved</option>
        </select>
    </label>
    <button @onclick="LoadTasks">Refresh</button>
</div>

@if (tasks == null)
{
    <p>Loading...</p>
}
else if (!tasks.Any())
{
    <p>No tasks found.</p>
}
else
{
    <table class="table">
        <thead>
            <tr>
                <th>Subscription</th>
                <th>Query</th>
                <th>Recipient</th>
                <th>Result Count</th>
                <th>Created</th>
                <th>Status</th>
                <th>Actions</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var task in tasks)
            {
                <tr>
                    <td>@task.SubscriptionName</td>
                    <td>@task.QueryName</td>
                    <td>@task.RecipientName</td>
                    <td>@task.ResultCount</td>
                    <td>@task.CreatedAt.ToShortDateString()</td>
                    <td>
                        @if (task.Resolved)
                        {
                            <span class="badge bg-success">Resolved</span>
                        }
                        else
                        {
                            <span class="badge bg-warning">Unresolved</span>
                        }
                    </td>
                    <td>
                        <button @onclick="() => ViewDetails(task.Id)">View</button>
                    </td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    private List<TaskData>? tasks;
    private string? resolvedFilter;

    protected override async Task OnInitializedAsync()
    {
        await LoadTasks();
    }

    private async Task LoadTasks()
    {
        bool? resolved = resolvedFilter switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };

        tasks = await TaskService.GetTasks(null, null, resolved, CancellationToken.None);
    }

    private void ViewDetails(int taskId)
    {
        Navigation.NavigateTo($"/tasks/{taskId}");
    }
}
```

---

#### Step 5.2: Create Task Details Page

**File**: `Semantico.UI/Components/Pages/Tasks/TaskDetails.razor`

```razor
@page "/tasks/{TaskId:int}"
@using Semantico.Core.Services
@using Semantico.Core.DTOs
@inject ITaskService TaskService

<h3>Task Details</h3>

@if (taskDetails == null)
{
    <p>Loading...</p>
}
else
{
    <dl>
        <dt>Subscription</dt>
        <dd>@taskDetails.Subscription.Name</dd>

        <dt>Recipient</dt>
        <dd>@taskDetails.Recipient.Name</dd>

        <dt>Result Count</dt>
        <dd>@taskDetails.ResultCount</dd>

        <dt>Created</dt>
        <dd>@taskDetails.CreatedAt</dd>

        <dt>Status</dt>
        <dd>@(taskDetails.Resolved ? "Resolved" : "Unresolved")</dd>

        @if (taskDetails.Resolved)
        {
            <dt>Resolved At</dt>
            <dd>@taskDetails.ResolvedAt</dd>

            <dt>Resolution Notes</dt>
            <dd>@(taskDetails.ResolutionNotes ?? "None")</dd>
        }
    </dl>

    @if (!taskDetails.Resolved)
    {
        <button @onclick="ShowResolveDialog">Resolve Task</button>
    }
    else
    {
        <button @onclick="ReopenTask">Reopen Task</button>
    }
}

@code {
    [Parameter]
    public int TaskId { get; set; }

    private TaskDetailsData? taskDetails;

    protected override async Task OnInitializedAsync()
    {
        taskDetails = await TaskService.GetTaskDetails(TaskId, CancellationToken.None);
    }

    private void ShowResolveDialog()
    {
        // TODO: Show resolve dialog
    }

    private async Task ReopenTask()
    {
        await TaskService.ReopenTask(TaskId, CancellationToken.None);
        taskDetails = await TaskService.GetTaskDetails(TaskId, CancellationToken.None);
    }
}
```

---

#### Step 5.3: Update Navigation

**File**: `Semantico.UI/Components/Layout/NavMenu.razor`

**Action**: Add Tasks navigation link

```razor
<NavLink class="nav-link" href="tasks">
    <span class="oi oi-task" aria-hidden="true"></span> Tasks
</NavLink>
```

**Verification**: Run application and verify Tasks page loads

```bash
dotnet watch run --project Semantico.SampleProject
```

---

### Phase 6: Testing & Verification (1-2 hours)

#### Step 6.1: Manual Testing Checklist

- [ ] Create Tasks recipient via Recipients page
- [ ] Attach Tasks recipient to subscription
- [ ] Trigger subscription manually (or wait for scheduled execution)
- [ ] Verify task created in database
- [ ] View task in Tasks page
- [ ] Resolve task with notes
- [ ] Verify resolved status and timestamp
- [ ] Reopen resolved task
- [ ] Verify task reopened successfully

---

#### Step 6.2: Integration Testing

Create test file: `Semantico.Tests/Features/Tasks/CreateTaskTests.cs`

```csharp
// TODO: Add integration tests for handlers
```

---

## Common Issues & Troubleshooting

### Issue: Migration fails with schema reference error
**Solution**: Ensure `Program.cs` uses default "semantico" schema temporarily for migration generation

### Issue: TasksAdapter not found in AdapterFactory
**Solution**: Verify TasksAdapter is registered in `GetAdapterService` switch statement

### Issue: Tasks page shows compilation errors
**Solution**: Verify `ITaskService` is registered in DI container

### Issue: Foreign key constraint violation when creating task
**Solution**: Verify NotificationId references valid Notification record with Tasks recipient

---

## Next Steps

After completing P1 + P2 implementation:

1. **Run `/speckit.tasks`**: Generate detailed implementation tasks
2. **Implement P3 (Timeline)**: Add task timeline visualization on subscription details page
3. **Implement P4 (Assignment)**: Add task assignment and notification features
4. **Performance Testing**: Test with 10,000+ tasks to validate index performance
5. **Documentation**: Update user documentation with Tasks recipient usage guide

---

## Summary

This quickstart provides step-by-step implementation for:
- ✅ Task entity and database schema (Phase 1)
- ✅ CQRS handlers for all task operations (Phase 2)
- ✅ TasksAdapter for notification handling (Phase 3)
- ✅ Service layer for UI integration (Phase 4)
- ✅ Blazor UI components for task management (Phase 5)
- ✅ Testing and verification procedures (Phase 6)

Follow phases in order for clean, incremental implementation. Each phase builds on previous phases and can be tested independently.
