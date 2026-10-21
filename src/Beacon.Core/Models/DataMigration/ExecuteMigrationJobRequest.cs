using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataMigration;

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
)
{
    /// <summary>
    /// The run did not start because the job already has a run starting or in progress (<see cref="ErrorMessage"/> says
    /// so): nothing was read, written or added to the job's history. The HTTP route answers 409.
    /// </summary>
    public bool RunInProgress { get; init; }
}