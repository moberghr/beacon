using Semantico.Core.Abstractions;
using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities.DataMigration;

public class MigrationExecutionHistory : BaseEntity, IExecutionHistory
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
    public required string ExecutedQuery { get; set; }
    public string? QueryParameters { get; set; }  // JSON

    // Retry Information
    public int RetryAttempt { get; set; } = 0;
    public int? ParentExecutionId { get; set; }  // For retry tracking
    public MigrationExecutionHistory? ParentExecution { get; set; }
    
    // Progress Tracking
    public int? EstimatedTotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public decimal ProgressPercentage => EstimatedTotalRows > 0 ? (decimal)ProcessedRows / EstimatedTotalRows.Value * 100 : 0;

    // IExecutionHistory implementation (most properties already naturally match the interface)
    bool IExecutionHistory.Success => Status == MigrationStatus.Completed || Status == MigrationStatus.PartialSuccess;
}