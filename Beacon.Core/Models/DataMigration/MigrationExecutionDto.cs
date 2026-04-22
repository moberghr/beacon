using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataMigration;

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