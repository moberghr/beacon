# Data Migration Tool - Data Model

## Entity Design

### MigrationJob Entity
Represents a configured data migration task that can be scheduled and executed repeatedly.

```csharp
public class MigrationJob : ArchivableBaseEntity, IChangeableEntity
{
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    
    // Query Configuration
    public int ProjectId { get; set; }  // Source project
    public Project Project { get; set; } = null!;
    public string QueryText { get; set; } = null!;
    
    // Destination Configuration  
    public int DestinationProjectId { get; set; }  // Target project
    public Project DestinationProject { get; set; } = null!;
    public string DestinationTable { get; set; } = null!;
    public MigrationMode Mode { get; set; } = MigrationMode.Insert;
    
    // Scheduling & Execution
    public bool IsEnabled { get; set; } = true;
    public string? Schedule { get; set; }  // Cron expression
    public int MaxRetries { get; set; } = 3;
    public int TimeoutMinutes { get; set; } = 30;
    
    // Validation & Transformation
    public bool ValidateBeforeExecution { get; set; } = true;
    public string? TransformationScript { get; set; }  // @result syntax transformations
    
    // Relationships
    public ICollection<MigrationExecution> Executions { get; set; } = new List<MigrationExecution>();
    
    // Auditing (from IChangeableEntity)
    public string? ChangedBy { get; set; }
    public DateTime? ChangedOn { get; set; }
}
```

### MigrationExecution Entity
Records details of each migration job execution, extending QueryExecutionHistory patterns.

```csharp
public class MigrationExecution : BaseEntity
{
    public int MigrationJobId { get; set; }
    public MigrationJob MigrationJob { get; set; } = null!;
    
    // Execution Details
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public MigrationStatus Status { get; set; } = MigrationStatus.Running;
    public string? ErrorMessage { get; set; }
    
    // Data Metrics
    public int SourceRowsRead { get; set; }
    public int DestinationRowsWritten { get; set; }
    public int RowsSkipped { get; set; }
    public int RowsFailed { get; set; }
    
    // Performance Metrics
    public TimeSpan ExecutionDuration => CompletedAt?.Subtract(StartedAt) ?? TimeSpan.Zero;
    public double RowsPerSecond => ExecutionDuration.TotalSeconds > 0 ? SourceRowsRead / ExecutionDuration.TotalSeconds : 0;
    
    // Query Context (following QueryExecutionHistory pattern)
    public string ExecutedQuery { get; set; } = null!;
    public string? QueryParameters { get; set; }  // JSON
    public string? TransformationApplied { get; set; }
    
    // Retry Information
    public int RetryAttempt { get; set; } = 0;
    public int? ParentExecutionId { get; set; }  // For retry tracking
    public MigrationExecution? ParentExecution { get; set; }
    
    // Progress Tracking
    public int? EstimatedTotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public decimal ProgressPercentage => EstimatedTotalRows > 0 ? (decimal)ProcessedRows / EstimatedTotalRows * 100 : 0;
}
```

## Enumerations

### MigrationMode
```csharp
public enum MigrationMode
{
    Insert = 1,        // Insert new rows only
    Upsert = 2,        // Insert or update based on key
    Truncate = 3       // Truncate destination before insert
}
```

### MigrationStatus
```csharp
public enum MigrationStatus
{
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    PartialSuccess = 6  // Some rows failed but execution completed
}
```

## Database Schema Extensions

### New Tables
- `MigrationJobs` - Primary migration job configuration
- `MigrationExecutions` - Execution history and metrics

### Indexes
```sql
-- Performance indexes for migration operations
CREATE INDEX IX_MigrationJobs_ProjectId ON MigrationJobs (ProjectId);
CREATE INDEX IX_MigrationJobs_DestinationProjectId ON MigrationJobs (DestinationProjectId);
CREATE INDEX IX_MigrationJobs_IsEnabled_IsArchived ON MigrationJobs (IsEnabled, IsArchived);

CREATE INDEX IX_MigrationExecutions_MigrationJobId ON MigrationExecutions (MigrationJobId);
CREATE INDEX IX_MigrationExecutions_Status_StartedAt ON MigrationExecutions (Status, StartedAt);
CREATE INDEX IX_MigrationExecutions_StartedAt ON MigrationExecutions (StartedAt DESC);
```

### Foreign Key Relationships
- `MigrationJob.ProjectId → Project.Id` (source database)
- `MigrationJob.DestinationProjectId → Project.Id` (destination database)
- `MigrationExecution.MigrationJobId → MigrationJob.Id`
- `MigrationExecution.ParentExecutionId → MigrationExecution.Id` (self-reference for retries)

## Entity Configuration (OnModelCreating)

```csharp
// MigrationJob configuration
modelBuilder.Entity<MigrationJob>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
    entity.Property(e => e.Description).HasMaxLength(1000).IsRequired();
    entity.Property(e => e.QueryText).IsRequired();
    entity.Property(e => e.DestinationTable).HasMaxLength(100).IsRequired();
    entity.Property(e => e.Schedule).HasMaxLength(50);
    entity.Property(e => e.TransformationScript);
    
    // Relationships
    entity.HasOne(e => e.Project)
          .WithMany()
          .HasForeignKey(e => e.ProjectId)
          .OnDelete(DeleteBehavior.Restrict);
          
    entity.HasOne(e => e.DestinationProject)
          .WithMany()
          .HasForeignKey(e => e.DestinationProjectId)
          .OnDelete(DeleteBehavior.Restrict);
          
    entity.HasMany(e => e.Executions)
          .WithOne(e => e.MigrationJob)
          .HasForeignKey(e => e.MigrationJobId);
});

// MigrationExecution configuration
modelBuilder.Entity<MigrationExecution>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.ExecutedQuery).IsRequired();
    entity.Property(e => e.ErrorMessage).HasMaxLength(4000);
    entity.Property(e => e.QueryParameters);
    entity.Property(e => e.TransformationApplied);
    
    // Self-reference for retry tracking
    entity.HasOne(e => e.ParentExecution)
          .WithMany()
          .HasForeignKey(e => e.ParentExecutionId)
          .OnDelete(DeleteBehavior.SetNull);
});
```

## Validation Rules

### MigrationJob Validation
- Name: Required, 1-200 characters, unique per project
- Description: Required, 1-1000 characters
- QueryText: Required, valid SQL syntax
- ProjectId: Must exist and be accessible
- DestinationProjectId: Must exist and be accessible, can equal ProjectId
- DestinationTable: Required, valid table name format
- Schedule: Must be valid cron expression if provided
- MaxRetries: 0-10 range
- TimeoutMinutes: 1-1440 range (24 hours max)

### MigrationExecution Validation
- StartedAt: Required, cannot be in future
- Status transitions: Queued → Running → (Completed|Failed|Cancelled|PartialSuccess)
- Row counts: Non-negative integers
- RetryAttempt: Must be <= MigrationJob.MaxRetries

## State Transitions

### MigrationJob States
- **Created** → Enabled/Disabled
- **Enabled** → Can be executed, scheduled
- **Disabled** → Cannot be executed
- **Archived** → Soft-deleted, executions preserved

### MigrationExecution States
- **Queued** → Waiting for execution
- **Running** → Currently executing
- **Completed** → Successfully finished
- **Failed** → Error occurred, may retry
- **Cancelled** → User cancelled
- **PartialSuccess** → Completed with some row failures