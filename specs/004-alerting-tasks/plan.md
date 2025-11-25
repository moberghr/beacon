# Implementation Plan: Alerting Tasks Recipient

**Branch**: `004-alerting-tasks` | **Date**: 2025-11-12 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/004-alerting-tasks/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Add "Tasks" as a new notification recipient type in the Semantico alerting system. When a subscription with a Tasks recipient executes and returns results, the system creates an internal task record that users can view, filter, resolve, and track over time. This provides an internal notification channel for workflow management without requiring external system integration, enabling users to manage data quality alerts, compliance violations, and operational issues directly within Semantico.

**Technical Approach**: Add Tasks as an internal notification type that creates task records directly via TaskService (no adapter needed - tasks are internal database records, not external notifications). Implement Jira-style task tracking where one task per subscription-recipient pair tracks multiple notifications over time. Tasks auto-resolve when query returns 0 results. Build Blazor UI components for task list, detail views with notification history, and resolution workflow.

## Technical Context

**Language/Version**: C# 12 / .NET 8.0
**Primary Dependencies**: EF Core 8.0, MediatR, Hangfire (existing scheduler), Blazor Server
**Storage**: PostgreSQL (primary) and SQL Server (secondary) via provider-specific projects
**Testing**: xUnit with FluentAssertions (following existing Semantico.Tests patterns)
**Target Platform**: Web application (Blazor Server on Linux/Windows)
**Project Type**: Multi-project solution (Core, UI, Provider-specific projects)
**Performance Goals**:
- Task creation: <5 seconds from query execution completion
- Task list loading: <2 seconds for 1000+ tasks
- Task resolution: <1 second
- Client-side filtering/sorting without page reload
**Constraints**:
- Must follow existing adapter pattern (IAdapter interface)
- Must maintain schema-agnostic migrations
- Must support multi-provider database compatibility
- Task creation must be synchronous within notification flow
- Must follow Clean Architecture with Core domain isolation
**Scale/Scope**:
- Estimated 2 entities modified (AlertingTask new, Notification modified), 1 service (TaskService)
- 4 Blazor UI components (task list, task details, resolve dialog, nav updates)
- 2 database migrations (PostgreSQL + SQL Server) - adds Tasks table + Notification.TaskId column
- Expected usage: 10-1,000 tasks per tenant over time (one task per subscription-recipient pair, much fewer than one-per-notification)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### ✅ I. Clean Architecture
- **Compliance**: Task entity will reside in `Semantico.Core/Data/Entities/`
- **Compliance**: TasksAdapter will reside in `Semantico.Core/Adapters/`
- **Compliance**: Task entity will inherit from `BaseArchivableEntity` for soft delete support
- **Compliance**: CQRS handlers will use MediatR in `Semantico.Core/Features/Tasks/`
- **Compliance**: UI components will reside in `Semantico.UI/Components/Pages/Tasks/`
- **Compliance**: Dependencies flow inward: UI → Services → Core (no violations)
- **Note**: Task entity will implement standard entity patterns (IChangeableEntity not needed as tasks are immutable after resolution)

### ✅ II. Schema-Agnostic Database Design
- **Compliance**: Migrations will be generated using default "semantico" schema
- **Compliance**: No hardcoded schema references in migrations
- **Compliance**: Schema applied at runtime via existing `modelBuilder.HasDefaultSchema(DefaultSchema)` in SemanticoContext
- **Compliance**: Task entity registration will follow existing patterns in `OnModelCreating`

### ✅ III. Multi-Provider Database Support
- **Compliance**: PostgreSQL migration: `Semantico.Core.PostgreSql`
- **Compliance**: SQL Server migration: `Semantico.Core.SqlServer`
- **Compliance**: Task entity uses provider-agnostic EF Core features (no PostgreSQL/SQL Server-specific types)
- **Compliance**: Will test migration and runtime behavior on both providers

### ✅ IV. Handler-Based Command/Query Pattern (CQRS)
- **Compliance**: All task operations implemented as MediatR handlers:
  - `CreateTask.Handler` (internal to TasksAdapter)
  - `ResolveTask.Handler`
  - `ReopenTask.Handler`
  - `GetTasks.Handler` (list with filtering)
  - `GetTaskDetails.Handler`
- **Compliance**: Handlers will be `internal sealed class` with primary constructor injection
- **Compliance**: Request/Response records defined at file end

### ✅ V. Strong Typing and Explicit Contracts
- **Compliance**: Task entity will use strong typing (required fields with `null!`, nullable fields explicit)
- **Compliance**: DTOs for task operations (TaskData, TaskDetailsData, etc.)
- **Compliance**: Indexes will be added for frequently queried fields (SubscriptionId, RecipientId, Resolved status, CreatedTime)

### ✅ VI. Code Style Consistency
- **Compliance**: PascalCase for Task entity, TasksAdapter, handler classes
- **Compliance**: camelCase for parameters and local variables
- **Compliance**: System namespaces → third-party → project imports
- **Compliance**: Custom exceptions via SemanticoException if needed

### ✅ Database Operations Standards
- **Compliance**: User will manually generate migrations as per CLAUDE.md:
  - `dotnet ef migrations add AddTaskEntity --project Semantico.Core.PostgreSql --startup-project Semantico.SampleProject`
  - `dotnet ef migrations add AddTaskEntity --project Semantico.Core.SqlServer --startup-project Semantico.SampleProject`
- **Compliance**: Migrations will be validated for schema-agnostic design before commit

### ✅ Build and Development Standards
- **Compliance**: Build with `dotnet build --property WarningLevel=0`
- **Compliance**: Test with `dotnet test` (existing test infrastructure)
- **Compliance**: Handlers testable without infrastructure dependencies

**GATE STATUS**: ✅ PASS - No constitutional violations. All principles followed.

## Project Structure

### Documentation (this feature)

```text
specs/004-alerting-tasks/
├── spec.md              # Feature specification
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
│   └── task-contracts.md  # CQRS handler contracts
├── checklists/
│   └── requirements.md  # Specification quality checklist (already created)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
Semantico.Core/
├── Data/
│   ├── Entities/
│   │   └── Task.cs                          # NEW: Task entity
│   ├── Enums/
│   │   └── NotificationType.cs              # MODIFIED: Add Tasks = 4
│   └── SemanticoContext.cs                  # MODIFIED: Add DbSet<Task>, configure indexes
├── Adapters/
│   ├── IAdapter.cs                           # EXISTING: Used by Email/Teams/Jira (Tasks doesn't use adapter)
│   └── AdapterFactory.cs                     # EXISTING: Not modified (Tasks bypasses adapter pattern)
├── Services/
│   ├── NotificationService.cs                # MODIFIED: Call TaskService directly for Tasks notifications
│   ├── ITaskService.cs                       # NEW: Task service interface
│   └── TaskService.cs                        # NEW: Find-or-create tasks, resolve, reopen, query methods
└── DTOs/
    ├── TaskData.cs                           # NEW: Task list item DTO
    └── TaskDetailsData.cs                    # NEW: Task details DTO

Semantico.Core.PostgreSql/
└── Migrations/
    └── [Timestamp]_AddTaskEntity.cs          # NEW: PostgreSQL migration (user generates)

Semantico.Core.SqlServer/
└── Migrations/
    └── [Timestamp]_AddTaskEntity.cs          # NEW: SQL Server migration (user generates)

Semantico.UI/
└── Components/
    └── Pages/
        └── Tasks/                             # NEW: Task UI components
            ├── Tasks.razor                    # Task list page with filters
            ├── TaskDetails.razor              # Task details view
            ├── ResolveTaskDialog.razor        # Dialog for resolving task
            └── TaskFilters.razor              # Filter component (status, subscription, recipient)

Semantico.SampleProject/
└── Program.cs                                 # MODIFIED: No changes needed (existing adapter registration)
```

**Structure Decision**: Multi-project solution following existing Semantico architecture. Task entity and business logic reside in `Semantico.Core` (Clean Architecture core), provider-specific migrations in `Semantico.Core.PostgreSql` and `Semantico.Core.SqlServer`, and UI components in `Semantico.UI`. This maintains clean separation of concerns and multi-provider database support per constitutional principles I-III.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

No violations. All constitutional principles are followed without exception.
