using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.DataMigration;

public record ExecuteMigrationJobRequest(
    int MigrationJobId,
    bool IsManualExecution = true,
    string? ExecutionContext = null,
    Dictionary<string, object>? Parameters = null
);

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