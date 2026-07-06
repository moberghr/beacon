using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataMigration;

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

public record GetMigrationExecutionsResponse(
    List<MigrationExecutionDto> Executions,
    int TotalCount,
    bool HasMore
);