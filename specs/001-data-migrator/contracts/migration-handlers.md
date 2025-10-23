# Migration Handler Contracts

## Handler Interfaces

### CreateMigrationJobHandler
```csharp
internal sealed class CreateMigrationJobHandler(
    SemanticoContext context,
    ILogger<CreateMigrationJobHandler> logger
) : IRequestHandler<CreateMigrationJobRequest, CreateMigrationJobResponse>
{
    public async Task<CreateMigrationJobResponse> Handle(
        CreateMigrationJobRequest request, 
        CancellationToken cancellationToken);
}
```

**Request Contract**:
```csharp
public record CreateMigrationJobRequest(
    string Name,
    string Description,
    int ProjectId,
    string QueryText,
    int DestinationProjectId,
    string DestinationTable,
    MigrationMode Mode = MigrationMode.Insert,
    bool IsEnabled = true,
    string? Schedule = null,
    int MaxRetries = 3,
    int TimeoutMinutes = 30,
    bool ValidateBeforeExecution = true,
    string? TransformationScript = null
);
```

**Response Contract**:
```csharp
public record CreateMigrationJobResponse(
    int MigrationJobId,
    bool Success,
    string? ErrorMessage = null,
    ValidationResult? ValidationResult = null
);

public record ValidationResult(
    bool IsValid,
    List<string> Errors,
    bool QueryValidated,
    bool DestinationValidated,
    int? EstimatedSourceRows = null
);
```

### ExecuteMigrationJobHandler
```csharp
internal sealed class ExecuteMigrationJobHandler(
    SemanticoContext context,
    IQueryService queryService,
    ILogger<ExecuteMigrationJobHandler> logger
) : IRequestHandler<ExecuteMigrationJobRequest, ExecuteMigrationJobResponse>
{
    public async Task<ExecuteMigrationJobResponse> Handle(
        ExecuteMigrationJobRequest request, 
        CancellationToken cancellationToken);
}
```

**Request Contract**:
```csharp
public record ExecuteMigrationJobRequest(
    int MigrationJobId,
    bool IsManualExecution = true,
    string? ExecutionContext = null,
    Dictionary<string, object>? Parameters = null
);
```

**Response Contract**:
```csharp
public record ExecuteMigrationJobResponse(
    int ExecutionId,
    MigrationStatus Status,
    int SourceRowsRead = 0,
    int DestinationRowsWritten = 0,
    int RowsSkipped = 0,
    int RowsFailed = 0,
    TimeSpan ExecutionDuration = default,
    string? ErrorMessage = null,
    List<string> Warnings = null
);
```

### GetMigrationJobsHandler
```csharp
internal sealed class GetMigrationJobsHandler(
    SemanticoContext context
) : IRequestHandler<GetMigrationJobsRequest, GetMigrationJobsResponse>
{
    public async Task<GetMigrationJobsResponse> Handle(
        GetMigrationJobsRequest request, 
        CancellationToken cancellationToken);
}
```

**Request Contract**:
```csharp
public record GetMigrationJobsRequest(
    int? ProjectId = null,
    bool? IsEnabled = null,
    bool IncludeArchived = false,
    int Skip = 0,
    int Take = 50,
    string? SearchTerm = null,
    MigrationJobSortBy SortBy = MigrationJobSortBy.Name,
    bool SortDescending = false
);

public enum MigrationJobSortBy
{
    Name,
    CreatedDate,
    LastExecuted,
    Status
}
```

**Response Contract**:
```csharp
public record GetMigrationJobsResponse(
    List<MigrationJobDto> Jobs,
    int TotalCount,
    bool HasMore
);

public record MigrationJobDto(
    int Id,
    string Name,
    string Description,
    int ProjectId,
    string ProjectName,
    int DestinationProjectId,
    string DestinationProjectName,
    string DestinationTable,
    MigrationMode Mode,
    bool IsEnabled,
    string? Schedule,
    DateTime CreatedDate,
    DateTime? LastExecuted,
    MigrationStatus? LastExecutionStatus,
    int TotalExecutions,
    int SuccessfulExecutions
);
```

### GetMigrationExecutionsHandler
```csharp
internal sealed class GetMigrationExecutionsHandler(
    SemanticoContext context
) : IRequestHandler<GetMigrationExecutionsRequest, GetMigrationExecutionsResponse>
{
    public async Task<GetMigrationExecutionsResponse> Handle(
        GetMigrationExecutionsRequest request, 
        CancellationToken cancellationToken);
}
```

**Request Contract**:
```csharp
public record GetMigrationExecutionsRequest(
    int? MigrationJobId = null,
    MigrationStatus? Status = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    int Skip = 0,
    int Take = 100,
    MigrationExecutionSortBy SortBy = MigrationExecutionSortBy.StartedAt,
    bool SortDescending = true
);

public enum MigrationExecutionSortBy
{
    StartedAt,
    CompletedAt,
    Status,
    RowsProcessed,
    Duration
}
```

**Response Contract**:
```csharp
public record GetMigrationExecutionsResponse(
    List<MigrationExecutionDto> Executions,
    int TotalCount,
    bool HasMore
);

public record MigrationExecutionDto(
    int Id,
    int MigrationJobId,
    string MigrationJobName,
    DateTime StartedAt,
    DateTime? CompletedAt,
    MigrationStatus Status,
    int SourceRowsRead,
    int DestinationRowsWritten,
    int RowsSkipped,
    int RowsFailed,
    TimeSpan ExecutionDuration,
    double RowsPerSecond,
    string? ErrorMessage,
    int RetryAttempt,
    bool IsRetry
);
```

### UpdateMigrationJobHandler
```csharp
internal sealed class UpdateMigrationJobHandler(
    SemanticoContext context,
    ILogger<UpdateMigrationJobHandler> logger
) : IRequestHandler<UpdateMigrationJobRequest, UpdateMigrationJobResponse>
{
    public async Task<UpdateMigrationJobResponse> Handle(
        UpdateMigrationJobRequest request, 
        CancellationToken cancellationToken);
}
```

**Request Contract**:
```csharp
public record UpdateMigrationJobRequest(
    int Id,
    string? Name = null,
    string? Description = null,
    string? QueryText = null,
    int? DestinationProjectId = null,
    string? DestinationTable = null,
    MigrationMode? Mode = null,
    bool? IsEnabled = null,
    string? Schedule = null,
    int? MaxRetries = null,
    int? TimeoutMinutes = null,
    bool? ValidateBeforeExecution = null,
    string? TransformationScript = null
);
```

**Response Contract**:
```csharp
public record UpdateMigrationJobResponse(
    bool Success,
    string? ErrorMessage = null,
    ValidationResult? ValidationResult = null
);
```

### DeleteMigrationJobHandler
```csharp
internal sealed class DeleteMigrationJobHandler(
    SemanticoContext context,
    ILogger<DeleteMigrationJobHandler> logger
) : IRequestHandler<DeleteMigrationJobRequest, DeleteMigrationJobResponse>
{
    public async Task<DeleteMigrationJobResponse> Handle(
        DeleteMigrationJobRequest request, 
        CancellationToken cancellationToken);
}
```

**Request Contract**:
```csharp
public record DeleteMigrationJobRequest(
    int Id,
    bool ForceDelete = false  // If true, hard delete; otherwise archive
);
```

**Response Contract**:
```csharp
public record DeleteMigrationJobResponse(
    bool Success,
    bool WasArchived,  // True if archived, false if hard deleted
    string? ErrorMessage = null
);
```

## Error Contracts

### Common Error Types
```csharp
public class MigrationJobNotFoundException : SemanticoException
{
    public MigrationJobNotFoundException(int migrationJobId) 
        : base($"Migration job with ID {migrationJobId} was not found.") { }
}

public class MigrationExecutionException : SemanticoException
{
    public MigrationExecutionException(string message, Exception? innerException = null) 
        : base(message, innerException) { }
}

public class MigrationValidationException : SemanticoException
{
    public List<string> ValidationErrors { get; }
    
    public MigrationValidationException(List<string> validationErrors) 
        : base($"Migration validation failed: {string.Join(", ", validationErrors)}")
    {
        ValidationErrors = validationErrors;
    }
}
```

## Handler Behavior Specifications

### CreateMigrationJobHandler Behavior
1. **Validation Phase**:
   - Validate all required fields
   - Check source project exists and is accessible
   - Check destination project exists and is accessible
   - Validate cron expression if schedule provided
   - Test query syntax against source database
   - Verify destination table exists or can be created

2. **Creation Phase**:
   - Create MigrationJob entity
   - Set audit fields (ChangedBy, ChangedOn)
   - Save to database with transaction

3. **Response**:
   - Return success with new ID
   - Include validation results
   - Return specific error messages for failures

### ExecuteMigrationJobHandler Behavior
1. **Pre-execution Phase**:
   - Load migration job and validate it exists and is enabled
   - Create MigrationExecution record with status=Queued
   - Validate query against current source schema

2. **Execution Phase**:
   - Update status to Running
   - Execute source query using existing IQueryService
   - Apply @result transformations if specified
   - Write data to destination using appropriate mode (Insert/Upsert/etc.)
   - Track row counts and performance metrics

3. **Completion Phase**:
   - Update execution record with final status and metrics
   - Handle retries for failed executions
   - Log execution details

4. **Error Handling**:
   - Capture detailed error information
   - Set appropriate status (Failed/PartialSuccess)
   - Prepare for retry if applicable