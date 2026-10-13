using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataMigration;

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
)
{
    /// <summary>
    /// The job was submitted for approval (the change approval workflow covers migration jobs): it does not run, manually
    /// or on its schedule, until a second person approves it.
    /// </summary>
    public bool AwaitingApproval { get; init; }
}

public record ValidationResult(
    bool IsValid,
    List<string> Errors,
    bool QueryValidated,
    bool DestinationValidated,
    int? EstimatedSourceRows = null
);