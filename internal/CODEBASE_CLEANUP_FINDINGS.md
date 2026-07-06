# Codebase Cleanup & Refactoring Findings

**Date:** 2025-11-28
**Analysis Scope:** Beacon.Core, Beacon.UI, Beacon.SampleProject

---

## Executive Summary

| Category | Issues Found | Estimated Lines Saved |
|----------|-------------|----------------------|
| Duplicate Code | 30+ patterns | ~1,840 lines |
| Redundant Logging | 26 issues | N/A |
| Abstraction Opportunities | 12 patterns | ~1,500+ lines |
| Code Quality Issues | 40+ issues | N/A |

**Critical Issues Requiring Immediate Attention:**
1. SQL Injection vulnerability in `QueryHelper.CompileSql()`
2. Hardcoded credentials in `Program.cs`
3. 15+ Console.WriteLine debug statements in production code

---

## 1. DUPLICATE CODE PATTERNS

### 1.1 Entity Configuration Duplication

**Files:**
- `Beacon.Core.PostgreSql/Data/PostgreSqlBeaconContext.cs:13-18`
- `Beacon.Core.SqlServer/Data/SqlServerBeaconContext.cs:13-18`

**Pattern:** Both override `OnModelCreating` with identical schema configuration code.

**Fix:** Move schema configuration to base `BeaconContext.OnModelCreating()`.

---

### 1.2 Service Collection Extension Duplication

**Files:**
- `Beacon.Core.PostgreSql/ServiceCollectionExtensions.cs:30-45`
- `Beacon.Core.SqlServer/ServiceCollectionExtensions.cs:29-44`

**Pattern:** Nearly identical `BeaconContextFactoryAdapter` class in both files (~32 lines each).

**Fix:** Create generic abstract base class `BeaconContextFactoryAdapterBase<T>` in shared location.

---

### 1.3 Query Parameter Transformation Duplication

**Files:**
- `Beacon.Core/Services/QueryService.cs:294-300, 343-349`
- `Beacon.Core/Services/DataSourceService.cs:118-135`
- `Beacon.Core/Services/SubscriptionService.cs` (throughout)

**Pattern:**
```csharp
Parameters = s.Parameters.Select(p => new QueryStepParameterData
{
    Name = p.Name,
    Type = p.Type,
    Description = p.Description,
    Placeholder = p.Placeholder
}).ToList()
```

**Fix:** Create extension method `ToQueryStepParameterDataList()` in `LinqHelpers.cs`.

---

### 1.4 Recipient Data Transformation Duplication

**Files:**
- `Beacon.Core/Services/SubscriptionService.cs:133-140, 243-250`
- `Beacon.Core/Services/NotificationService.cs:30-36, 154`
- `Beacon.Core/Services/QueryService.cs:438-445`

**Occurrences:** 5+ times

**Fix:** Create extension method `ToRecipientDataList()`.

---

### 1.5 Parameter Entity Creation Duplication

**Files:**
- `Beacon.Core/Services/QueryService.cs:215-225, 510-520, 546-556, 682-692, 726-736`
- `Beacon.Core/Services/SubscriptionService.cs:78-84, 210-220`

**Occurrences:** 7+ times

**Fix:** Create helper class `ParameterEntityFactory` with methods like `CreateQueryStepParameter()`.

---

### 1.6 Task Creation/Update Logic Duplication

**File:** `Beacon.Core/Services/TaskService.cs:16-96, 98-149`

**Pattern:** `CreateTask` and `CreateOrUpdateTask` share ~70% identical logic.

**Fix:** Extract helpers:
- `FindOrCreateUnresolvedTask()`
- `ApplyAutoResolutionIfNeeded()`

---

### 1.7 Recipient Fetching Duplication

**File:** `Beacon.Core/Services/SubscriptionService.cs:62-64, 189-191, 297-299`

**Occurrences:** 3 times

**Fix:** Create helper method `GetRecipientsByIds()`.

---

## 2. REDUNDANT LOGGING

### 2.1 Console.WriteLine in TaskService.cs (CRITICAL)

**File:** `Beacon.Core/Services/TaskService.cs`

**Issues:** 14 Console.WriteLine statements instead of ILogger:

| Line | Issue |
|------|-------|
| 18 | Method entry logging with all params |
| 28 | Debug: "Updating existing task..." |
| 45 | Debug: "Linking notification..." |
| 50 | Warning: Notification not found |
| 54 | Success message (remove) |
| 59 | Debug: "Creating new task..." |
| 78 | Success message (remove) |
| 84 | Debug: "Linking notification..." |
| 90 | **DUPLICATE** of line 50 |
| 93 | Success message (remove) |
| 100 | **DUPLICATE** of line 18 pattern |
| 110 | **DUPLICATE** of line 28 |
| 124 | **DUPLICATE** of line 54 |
| 131, 135, 147 | Verbose conditional logs |

**Fix:** Inject `ILogger<TaskService>` and replace all Console.WriteLine calls.

---

### 2.2 Console.WriteLine in JobService.cs

**File:** `Beacon.Core/Worker/Services/JobService.cs:94`

**Fix:** Replace with `_logger.LogDebug()`.

---

### 2.3 Placeholder Warning in SchedulingService.cs

**File:** `Beacon.Core/Services/Shared/SchedulingService.cs:71`

```csharp
_logger.LogWarning("GetNextExecutionTime not fully implemented, returning 1 hour from now")
```

**Issue:** Logs on every cron calculation, causes alert fatigue.

**Fix:** Implement feature or remove warning.

---

## 3. ABSTRACTION OPPORTUNITIES

### 3.1 Generic CRUD Service Base Class (CRITICAL)

**Services sharing identical patterns:**
- `QueryService.cs` (143-932)
- `SubscriptionService.cs` (33-305)
- `DataSourceService.cs` (26-225)
- `RecipientService.cs` (21-101)

**Proposed abstraction:**
```csharp
public abstract class CrudService<TEntity, TDto, TCreateRequest, TUpdateRequest>
    where TEntity : ArchivableBaseEntity
{
    protected readonly IDbContextFactory<BeaconContext> ContextFactory;

    protected virtual Task<BaseResponse> CreateAsync(TCreateRequest request, CancellationToken ct);
    protected virtual Task DeleteAsync(int id, CancellationToken ct);
    protected virtual Task<List<TDto>> GetAsync(..., CancellationToken ct);
}
```

**Estimated savings:** 400+ lines

---

### 3.2 Multi-Step Workflow Extensions

**Files:**
- `Beacon.Core/Data/Entities/Query.cs:26-34`
- `Beacon.Core/Data/Entities/DataMigration/MigrationJob.cs:63-67`

**Duplicated logic:**
```csharp
public bool IsMultiStep => Steps.Count > 1;
public bool IsCrossDataSource => Steps.Select(s => s.DataSourceId).Distinct().Count() > 1;
public bool IsCrossDatabase => Steps.Select(s => s.DataSource.DatabaseEngineType).Distinct().Count() > 1;
```

**Fix:** Create `MultiStepWorkflowExtensions` class with shared logic.

---

### 3.3 Fluent Validator Pattern

**Files with scattered validation:**
- `Beacon.Core/Validators/QueryValidator.cs:18-27`
- `Beacon.Core/Validators/SubscriptionValidator.cs:10-49`
- `Beacon.Core/Services/SubscriptionService.cs:40-60, 183-187`

**Proposed abstraction:**
```csharp
public class FluentValidator
{
    public FluentValidator ValidateCronExpression(string cron);
    public FluentValidator ValidateParameterCount(int supplied, int required);
    public FluentValidator ValidateRecipients(List<Recipient> recipients, bool createTasksMode);
    public void ThrowIfInvalid();
}
```

---

### 3.4 Dependency Checker Interface

**Pattern repeated in:**
- `QueryService.cs:248-251`
- `DataSourceService.cs:61-83`
- `RecipientService.cs:52-55`

**Proposed abstraction:**
```csharp
public interface IDependencyChecker<T> where T : ArchivableBaseEntity
{
    Task<DependencyCheckResult> CheckDependenciesAsync(T entity, CancellationToken ct);
}
```

---

### 3.5 Migration Executor Strategy Pattern

**File:** `Beacon.Core/Services/MigrationService.cs` (1343 lines!)

**Issue:** Multiple database-specific implementations with repeated code blocks.

**Proposed abstraction:**
```csharp
public interface IMigrationExecutor
{
    Task<(int rowsWritten, int rowsFailed, List<string> errors)> ExecuteInsertAsync(...);
    Task<(int rowsWritten, int rowsFailed, List<string> errors)> ExecuteUpsertAsync(...);
}

public class PostgreSqlMigrationExecutor : IMigrationExecutor { }
public class SqlServerMigrationExecutor : IMigrationExecutor { }
```

**Estimated savings:** 800+ lines

---

## 4. CODE QUALITY ISSUES

### 4.1 SQL Injection Vulnerability (CRITICAL)

**File:** `Beacon.Core/Helpers/QueryHelper.cs:7-15`

```csharp
public static string CompileSql(string querySql, List<SubscriptionParamaterData> parameterValues)
{
    foreach (var parameter in parameterValues)
    {
        querySql = querySql.Replace(parameter.QueryPlaceholder, parameter.Value);
    }
    return querySql;
}
```

**Issue:** Direct string replacement without parameterization allows SQL injection.

**Fix:** Use parameterized queries (Dapper parameters, NpgsqlParameterCollection, SqlParameterCollection).

---

### 4.2 Hardcoded Credentials (CRITICAL)

**File:** `Beacon.SampleProject/Program.cs:46, 57`

```csharp
options.BaseUrl = "https://localhost:7187/beacon";
.UseBasicAuthentication("admin", "admin")
```

**Fix:** Move to configuration files or environment variables.

---

### 4.3 Bare Exception Handling

**File:** `Beacon.Core/Services/QueryExecutionPreviewService.cs:25-32, 37-44, 49-56, 106-110, 161-164`

```csharp
try { ... }
catch { return null; }  // Swallows ALL exceptions silently
```

**Fix:** Catch specific exceptions, log before returning null.

---

### 4.4 Magic Numbers

| File | Line | Value | Should Be |
|------|------|-------|-----------|
| QueryService.cs | 447 | 1_000_000 | `DefaultMaxRows` |
| MigrationService.cs | 1081 | 10000 | `BulkInsertBatchSize` |
| MigrationService.cs | 1117 | 100 | `MaxFailedRowsBeforeStop` |
| MigrationService.cs | 846 | 300 | `BulkCopyTimeoutSeconds` |

---

### 4.5 Null Reference Risks

**File:** `Beacon.Core/Helpers/Helpers.cs:43, 71-72`

```csharp
queryResult.TopRecords.FirstOrDefault().SelectMany(property => ...)
```

**Issue:** Calls methods on `FirstOrDefault()` result without null check.

**Fix:** Add null check before accessing.

---

### 4.6 TODO Comments (Track in Issues)

| File | Line | TODO |
|------|------|------|
| MigrationService.cs | 65 | Get ChangedBy from current user context |
| MigrationService.cs | 414-418 | Add sophisticated validation |
| MigrationService.cs | 580-581 | Implement transformation logic |
| JobService.cs | 139 | Refactor to use Notifications table |

---

### 4.7 Typo in Class Name

**File:** `SubscriptionParamaterData.cs`

Class name has typo: `SubscriptionParamaterData` → should be `SubscriptionParameterData`

Used throughout: QueryService.cs, SubscriptionService.cs, MigrationService.cs

---

### 4.8 Brittle Exception Matching

**File:** `Beacon.Core/Services/MigrationService.cs:649-660`

```csharp
catch (Exception ex) when (ex.Message.Contains("does not exist"))
catch (Exception ex) when (ex.Message.Contains("connect"))
```

**Issue:** Message matching is brittle; different databases have different error messages.

**Fix:** Use specific exception types (SqlException, NpgsqlException).

---

## 5. PROPOSED NEW FILE STRUCTURE

```
Beacon.Core/
├── Helpers/
│   ├── LinqHelpers.cs (EXPAND - add extension methods)
│   ├── ParameterEntityFactory.cs (NEW)
│   ├── StatisticsHelper.cs (NEW)
│   └── MultiStepWorkflowExtensions.cs (NEW)
├── Services/
│   ├── Base/
│   │   └── CrudService.cs (NEW - generic base)
│   └── Migration/
│       ├── IMigrationExecutor.cs (NEW)
│       ├── PostgreSqlMigrationExecutor.cs (NEW)
│       └── SqlServerMigrationExecutor.cs (NEW)
├── Validators/
│   └── FluentValidator.cs (NEW)
├── Data/
│   └── Factories/
│       └── BeaconContextFactoryAdapterBase.cs (NEW)
```

---

## 6. PRIORITY ACTION ITEMS

### Critical (Address Immediately) - COMPLETED 2025-11-28
1. [x] Fix SQL injection in `QueryHelper.CompileSql()` - Added SQL value escaping
2. [x] Remove hardcoded credentials from `Program.cs` - Moved to configuration
3. [x] Replace Console.WriteLine with ILogger in `TaskService.cs` - Done

### High Priority (Address This Sprint) - COMPLETED 2025-11-28
1. [x] Fix bare exception handling in `QueryExecutionPreviewService.cs` - Added specific exception types and logging
2. [x] Add null checks in `Helpers.cs` (Adapters) - Fixed null reference issues in email/Jira content generation
3. [x] Create extension methods for common LINQ projections - Added to `LinqHelpers.cs`

### Medium Priority (Next Sprint) - MOSTLY COMPLETED
1. [~] Create generic `CrudService<T>` base class - Determined not necessary; services have different domain logic
2. [x] Extract magic numbers to constants - Created `Constants.cs`
3. [ ] Refactor `MigrationService.cs` with strategy pattern - DEFERRED (larger task ~200+ lines, recommend separate PR)
4. [x] Create `ParameterEntityFactory` helper class - Created factory with overloads for QueryStepParameterData, QueryParameterData, and SubscriptionParameterData
5. [x] Move schema configuration to base BeaconContext - Removed duplicate OnModelCreating overrides

### Low Priority (Technical Debt Backlog) - MOSTLY COMPLETED
1. [x] Rename `SubscriptionParamaterData` to fix typo - Renamed class and file, updated all references
2. [x] Move `INotificationService` to separate file - Done
3. [x] Implement SchedulingService placeholder methods - Now uses Cronos library
4. [x] Remove redundant field assignments (TaskService, JobService) - Now uses primary constructor parameters directly
5. [ ] Convert TODOs to GitHub Issues

---

## 7. REFACTORING QUICK WINS

### Extension Methods to Add to LinqHelpers.cs

```csharp
// 1. Parameter projection
public static List<QueryStepParameterData> ToQueryStepParameterDataList(
    this IEnumerable<QueryStepParameter> parameters)
{
    return parameters.Select(p => new QueryStepParameterData
    {
        Name = p.Name,
        Type = p.Type,
        Description = p.Description,
        Placeholder = p.Placeholder
    }).ToList();
}

// 2. Recipient projection
public static List<RecipientData> ToRecipientDataList(
    this IEnumerable<Recipient> recipients)
{
    return recipients.Select(r => new RecipientData
    {
        RecipientId = r.Id,
        Name = r.Name,
        Description = r.Description,
        Destination = r.Destination,
        NotificationType = r.NotificationType
    }).ToList();
}

// 3. Conditional filtering (already exists, could add more)
public static IQueryable<T> TakeIf<T>(this IQueryable<T> query, bool condition, int count)
    => condition ? query.Take(count) : query;
```

---

## 8. METRICS SUMMARY

| Metric | Value |
|--------|-------|
| Total duplicate code patterns | 30+ |
| Estimated lines of duplication | ~1,840 |
| Console.WriteLine statements to remove | 15 |
| Critical security issues | 2 |
| Services needing ILogger injection | 2 |
| TODO comments to track | 4 |
| Magic numbers to extract | 4+ |
| Exception handlers to fix | 5 |

---

---

## 9. IMPLEMENTATION LOG

### 2025-11-28 - Initial Cleanup Sprint

**Files Modified:**
- `Beacon.Core/Helpers/QueryHelper.cs` - Added SQL escaping, fixed class name reference
- `Beacon.Core/Constants.cs` - NEW FILE - Application constants
- `Beacon.SampleProject/Program.cs` - Moved credentials to configuration
- `Beacon.Core/Services/TaskService.cs` - Replaced Console.WriteLine with ILogger, removed redundant field assignments
- `Beacon.Core/Worker/Services/JobService.cs` - Replaced Console.WriteLine with ILogger, removed redundant field assignments
- `Beacon.Core/Services/QueryExecutionPreviewService.cs` - Added proper exception handling with logging
- `Beacon.Core/Helpers/LinqHelpers.cs` - Added extension methods (ToQueryStepParameterDataList, ToRecipientDataList, SkipIf)
- `Beacon.Core/Models/Subscriptions/SubscriptionParameterData.cs` - Renamed file and class (was SubscriptionParamaterData)
- `Beacon.Core/Services/SubscriptionService.cs` - Updated to use new class name
- `Beacon.Core/Validators/SubscriptionValidator.cs` - Updated to use new class name
- `Beacon.Core/Models/Subscriptions/SubscriptionData.cs` - Updated to use new class name
- `Beacon.Core/Models/Subscriptions/SubscriptionDetailsData.cs` - Updated to use new class name
- `Beacon.Core/Services/Shared/ParameterResolver.cs` - Updated to use new class name
- `Beacon.Core/Services/Shared/QueryExecutionOrchestrator.cs` - Updated to use new class name
- `Beacon.Core/Services/QueryService.cs` - Updated to use new class name and constants
- `Beacon.Core/Services/MigrationService.cs` - Replaced magic numbers with constants
- `Beacon.Core/Adapters/Helpers.cs` - Fixed null reference issues in email/Jira content generation
- `Beacon.Core/Services/INotificationService.cs` - NEW FILE - Extracted interface from NotificationService
- `Beacon.Core/Services/NotificationService.cs` - Removed interface (moved to separate file)
- `Beacon.Core/Services/Shared/SchedulingService.cs` - Implemented cron methods properly using Cronos library
- `Beacon.Core/Helpers/ParameterEntityFactory.cs` - NEW FILE - Factory for creating parameter entities from DTOs
- `Beacon.Core/Services/QueryService.cs` - Refactored 5 parameter creation sites to use ParameterEntityFactory
- `Beacon.Core/Services/SubscriptionService.cs` - Refactored 2 parameter creation sites to use ParameterEntityFactory
- `Beacon.Core/Services/TaskService.cs` - Extracted helper methods for task creation/update (FindUnresolvedTaskAsync, UpdateTaskWithResultCount, CreateNewTask, LinkNotificationToTaskAsync)
- `Beacon.Core/Data/BeaconContext.cs` - Added HasDefaultSchema call to base OnModelCreating
- `Beacon.Core.PostgreSql/Data/PostgreSqlBeaconContext.cs` - Removed duplicate OnModelCreating, converted to primary constructor
- `Beacon.Core.SqlServer/Data/SqlServerBeaconContext.cs` - Removed duplicate OnModelCreating, converted to primary constructor

**Summary:**
- Critical security issues: 2 fixed (SQL injection, hardcoded credentials)
- Code quality issues: 10 fixed (logging, exception handling, null checks, magic numbers, typo, interface separation, placeholder implementations, redundant code)
- New infrastructure: 3 files (Constants.cs, INotificationService.cs, ParameterEntityFactory.cs), 3 extension methods
- Duplicate code consolidated: 7 parameter creation patterns + task creation/update patterns + schema configuration
- Derived context classes simplified from 19 lines to 6 lines each

*This document should be updated as issues are addressed.*
