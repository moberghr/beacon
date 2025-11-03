using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.DataMigration;

public class CreateMigrationJobRequest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int DataSourceId { get; set; }
    public string QueryText { get; set; } = "";
    public int DestinationDataSourceId { get; set; }
    public string DestinationTable { get; set; } = "";
    public MigrationMode Mode { get; set; } = MigrationMode.Insert;
    public bool IsEnabled { get; set; } = true;
    public string? Schedule { get; set; }
    public int MaxRetries { get; set; } = 3;
    public int TimeoutMinutes { get; set; } = 30;
    public bool ValidateBeforeExecution { get; set; } = true;
    public string? TransformationScript { get; set; }
}

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