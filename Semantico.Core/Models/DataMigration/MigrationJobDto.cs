using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.DataMigration;

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

public record MigrationJobDetailsDto(
    int Id,
    string Name,
    string Description,
    int ProjectId,
    string QueryText,
    int DestinationProjectId,
    string DestinationTable,
    MigrationMode Mode,
    bool IsEnabled,
    string? Schedule,
    int MaxRetries,
    int TimeoutMinutes,
    bool ValidateBeforeExecution,
    string? TransformationScript
);