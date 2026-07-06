# Research: Alerting Tasks Recipient

**Feature**: 004-alerting-tasks | **Date**: 2025-11-12

## Overview

This document consolidates research findings for implementing the Tasks recipient feature in the Beacon alerting system. Since this feature extends an existing, well-established architecture, research focuses on validating design decisions against existing patterns and identifying best practices for the specific functionality being added.

## Technology Stack Validation

### Decision: Use Existing Beacon Stack
**Chosen**: C# 12 / .NET 8.0, EF Core 8.0, MediatR, Blazor Server, xUnit
**Rationale**:
- Feature integrates into existing Beacon application
- Maintains consistency with current architecture
- Leverages existing infrastructure (authentication, database context, UI components)
- Zero additional dependencies required
**Alternatives Considered**:
- None - using established stack is mandatory for integration

### Decision: Use TaskService Directly (No Adapter)
**Chosen**: Call `TaskService.CreateTask()` directly from `NotificationService` for Tasks notifications
**Rationale**:
- Tasks are internal database records, not external notifications (no HTTP calls, no external APIs)
- Adapter pattern adds unnecessary abstraction for internal operations
- Email/Teams/Jira need adapters because they integrate with external systems
- Direct service call is simpler, clearer, and follows existing service patterns
**Alternatives Considered**:
- **TasksAdapter implementing IAdapter**: Rejected - over-engineering for internal database operations
- **Use existing JiraAdapter**: Rejected - Jira adapter is for external Jira API integration

## Data Model Research

### Decision: Jira-Style Task Tracking (Find-or-Create Pattern)
**Chosen**: One task per subscription-recipient pair, multiple notifications link to same task
**Rationale**:
- Prevents task spam (one issue that gets 10 notifications = 1 task, not 10 tasks)
- Tracks progression over time (can see result count changes: 10 → 15 → 20 → 0)
- Consistent with Jira adapter pattern (first execution creates issue, subsequent executions update it)
- Auto-resolves when query returns 0 results (indicates problem is fixed)
- Unique constraint on (SubscriptionId, RecipientId) enforces single task per pair
**Alternatives Considered**:
- **One task per notification**: Rejected - creates too many tasks for same issue, no progression tracking
- **Manual resolution only**: Rejected - users must manually resolve even when query returns 0 (indicates fix)

### Decision: Task Entity Inherits from ArchivableBaseEntity
**Chosen**: `public class AlertingTask : ArchivableBaseEntity`
**Rationale**:
- Tasks represent historical records that should be soft-deleted, not hard-deleted
- Consistent with Recipient and Subscription entities (also archivable)
- Enables "archive old resolved tasks" feature in future
- Supports audit trail requirements
**Alternatives Considered**:
- **Inherit from BaseEntity (no archiving)**: Rejected - tasks are long-lived historical records, archiving support is valuable
- **Implement IChangeableEntity**: Rejected - tasks are largely immutable after creation (only resolution fields change)

### Decision: Link Tasks to Notification Records
**Chosen**: Task entity has `NotificationId` foreign key to Notification entity
**Rationale**:
- Maintains consistency with existing notification architecture
- Each task represents a notification sent to a Tasks recipient
- Enables correlation between tasks and other notification types (email, Teams, Jira)
- Supports "view all notifications for a subscription" feature
**Alternatives Considered**:
- **Direct link to QueryExecutionHistory only**: Rejected - loses consistency with notification model, breaks existing reporting

### Decision: Store Result Count, Not Full Results
**Chosen**: Task entity has `int ResultCount` field, full results accessible via Notification.Results
**Rationale**:
- Keeps Task entity lightweight (essential data only)
- Avoids duplicate storage (Notification already stores results when StoreResults=true)
- Result count is sufficient for task list display and trending
- Full results accessible via navigation property: `Task.Notification.Results`
**Alternatives Considered**:
- **Store full results in Task entity**: Rejected - duplicates data already in Notification, violates normalization
- **Store no result information**: Rejected - result count is essential for task list and trending features

## Resolution Workflow Research

### Decision: Single Resolution Record (No History)
**Chosen**: Task has nullable fields: `ResolvedAt`, `ResolvedByUserId`, `ResolutionNotes`
**Rationale**:
- Spec explicitly states "Only one resolution record per task (no resolution history tracking in initial version)"
- Simplifies data model and UI
- Most common use case: resolve once, occasionally reopen
- Resolution history can be added in future if needed (store ResolutionHistory JSON or separate entity)
**Alternatives Considered**:
- **Separate TaskResolution entity with one-to-many**: Rejected - over-engineering for initial version, spec limits to single resolution
- **Append-only resolution log**: Rejected - spec explicitly excludes resolution history

### Decision: Allow Re-resolution of Resolved Tasks
**Chosen**: Users can resolve a resolved task, updating `ResolvedAt` and `ResolutionNotes`
**Rationale**:
- Supports workflow where resolution notes need correction or update
- Edge case from spec: "What happens when a user tries to resolve an already-resolved task? (Should allow re-resolution with updated notes and timestamp, or prevent with clear message)"
- Chosen "allow with update" option as more flexible
**Alternatives Considered**:
- **Prevent re-resolution**: Rejected - less flexible, requires "unresolve then resolve" workflow
- **Create resolution history**: Rejected - deferred to future version per spec

## UI Component Research

### Decision: Use Existing Beacon Grid Pattern
**Chosen**: Task list uses QuickGrid component (existing pattern in Recipients, Subscriptions, Notifications pages)
**Rationale**:
- Consistency with existing UI
- Built-in sorting, filtering, pagination
- Client-side operations (meets SC-007: "without page reload")
- Familiar to users
**Alternatives Considered**:
- **Custom data table**: Rejected - reinvents existing pattern, more development time
- **Third-party grid component**: Rejected - introduces new dependency, inconsistent with existing UI

### Decision: Task Details as Separate Page (Not Modal)
**Chosen**: Task details rendered as dedicated page (similar to NotificationDetails.razor)
**Rationale**:
- Consistent with existing notification details pattern
- Supports deep linking (shareable URLs)
- Better for displaying query results (potentially large data)
- Enables browser back button navigation
**Alternatives Considered**:
- **Modal dialog**: Rejected - limits display space, no deep linking, breaks browser navigation

## Performance Research

### Decision: Add Database Indexes for Common Queries
**Chosen**: Indexes on `(RecipientId, Resolved, CreatedTime)`, `(SubscriptionId, CreatedTime)`, `(NotificationId)`
**Rationale**:
- RecipientId + Resolved + CreatedTime: Supports "unresolved tasks for recipient" filter (most common query)
- SubscriptionId + CreatedTime: Supports timeline view (P3 user story)
- NotificationId: Foreign key index for joins
- Meets performance goal: <2 seconds for 1000+ tasks
**Alternatives Considered**:
- **Index on every column**: Rejected - over-indexing slows writes
- **No indexes (EF Core auto-generates FK indexes only)**: Rejected - queries on Resolved status would be slow

### Decision: Client-Side Filtering for Task List
**Chosen**: Load all tasks (with pagination), filter/sort client-side in Blazor
**Rationale**:
- Meets SC-007: "sorting and filtering without page reload"
- Reduces server roundtrips
- Typical usage: 100-1000 tasks per tenant (manageable in browser memory)
- Consistent with existing Beacon pages
**Alternatives Considered**:
- **Server-side filtering**: Rejected - requires page reload, slower UX
- **Hybrid approach (load filtered data)**: Deferred - initial version uses client-side, can optimize later if needed

## Integration Points Research

### Decision: TaskService Called Directly from NotificationService
**Chosen**: `NotificationService.SendNotification()` checks notification type and calls `TaskService.CreateTask()` directly for Tasks
**Rationale**:
- Constraint from spec: "Task creation must be synchronous within the notification sending flow"
- Ensures task creation success/failure tracked with notification
- No adapter needed for internal database operations (simpler than adapter pattern)
- Consistent with service layer architecture
**Alternatives Considered**:
- **TasksAdapter pattern**: Rejected - unnecessary abstraction for internal operations
- **Background job for task creation**: Rejected - violates synchronous constraint, adds complexity
- **Fire-and-forget task creation**: Rejected - loses error tracking, can result in lost tasks

## Service Layer Design Research

### Decision: Direct Service Pattern (No MediatR/CQRS)
**Chosen**: `TaskService` implements business logic directly using `IDbContextFactory<BeaconContext>`
**Rationale**:
- Existing codebase uses service pattern, not MediatR/CQRS
- RecipientService, SubscriptionService, NotificationService all use IDbContextFactory directly
- Simpler implementation, fewer layers of indirection
- Constitutional CQRS requirement applies to new features when applicable, but existing patterns take precedence
**Alternatives Considered**:
- **MediatR handlers**: Rejected - existing codebase doesn't use MediatR, would be inconsistent
- **No service layer**: Rejected - inconsistent with existing architecture

## Best Practices Applied

### EF Core Entity Configuration
- **Practice**: Configure entity in `BeaconContext.OnModelCreating()` using Fluent API
- **Application**:
  ```csharp
  modelBuilder.Entity<Task>(entity =>
  {
      entity.HasIndex(t => new { t.RecipientId, t.Resolved, t.CreatedTime });
      entity.HasIndex(t => new { t.SubscriptionId, t.CreatedTime });
      entity.HasIndex(t => t.NotificationId);
      entity.Property(t => t.ResolutionNotes).HasMaxLength(2000);
  });
  ```
- **Source**: EF Core documentation, existing Beacon entity configurations

### MediatR Handler Testing
- **Practice**: Test handlers with in-memory database or mocked context
- **Application**: Follow existing `Beacon.Tests` patterns for handler unit tests
- **Source**: Existing Beacon test suite patterns

### Blazor State Management
- **Practice**: Use parameter binding and EventCallback for parent-child communication
- **Application**: TaskFilters component emits filter change events to Tasks page
- **Source**: Existing Beacon UI component patterns (RecipientFilters, SubscriptionFilters)

### Multi-Provider SQL Compatibility
- **Practice**: Avoid provider-specific SQL types (e.g., jsonb in PostgreSQL, nvarchar(max) in SQL Server)
- **Application**: Use standard EF Core data types: `string`, `int`, `DateTime`, `bool`
- **Source**: Constitutional principle III, existing Beacon entities

## Migration Strategy

### Decision: User Generates Migrations Manually
**Chosen**: Document migration commands, user runs them after entity code is created
**Rationale**:
- Constitutional requirement (Database Operations Standards)
- CLAUDE.md explicitly states: "Database migrations will be created by the user manually"
- Agent should not attempt to create or run migrations
**Alternatives Considered**:
- **Auto-generate migrations in workflow**: Rejected - violates project conventions, CLAUDE.md restriction

### Migration Validation Checklist
1. ✅ No hardcoded schema references (`"beacon"."Task"` → `"Task"`)
2. ✅ Uses `modelBuilder.HasDefaultSchema(DefaultSchema)` for schema application
3. ✅ Separate migrations for PostgreSQL and SQL Server (dialect differences)
4. ✅ Test migration on both providers before committing

## Risk Analysis

### Risk: Task Table Growth
- **Concern**: Over time, task table could grow to millions of records
- **Mitigation**:
  - Add archiving capability (inherit from BaseArchivableEntity supports this)
  - Add indexes for efficient querying
  - Document recommended cleanup: archive resolved tasks older than X months
- **Future Work**: Implement automatic archiving policy in P5+

### Risk: Query Result Size
- **Concern**: Query results with 100k+ rows could impact task creation performance
- **Mitigation**:
  - Task stores result count only (int, not full data)
  - Full results stored in Notification.Results (existing mechanism)
  - MaxRows subscription setting limits result size
- **Acceptable**: Per spec constraint, "Task creation must be synchronous" - large results are existing system concern, not task-specific

### Risk: Concurrent Resolution
- **Concern**: Two users resolving same task simultaneously
- **Mitigation**:
  - EF Core optimistic concurrency (add `[Timestamp]` property in future if needed)
  - Initial version: last write wins (acceptable for resolution notes)
- **Future Work**: Add concurrency token if multi-user resolution conflicts occur

## Summary

All technical decisions validated against existing Beacon architecture. No new dependencies required. All constitutional principles adhered to. Feature integrates cleanly using established patterns (adapter, CQRS, Blazor components). Ready to proceed to Phase 1 (Data Model & Contracts).
