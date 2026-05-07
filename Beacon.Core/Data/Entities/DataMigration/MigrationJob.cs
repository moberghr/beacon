using System.Text.Json;
using Beacon.Core.Abstractions;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Data.Entities.DataMigration;

public class MigrationJob : ArchivableBaseEntity, IScheduledJob, IMultiStepWorkflow
{
    public required string Name { get; set; }
    public required string Description { get; set; }

    // Query Configuration
    public int DataSourceId { get; set; }
    public DataSource DataSource { get; set; } = null!;
    public required string QueryText { get; set; }

    // Destination Configuration
    public int DestinationDataSourceId { get; set; }
    public DataSource DestinationDataSource { get; set; } = null!;
    public required string DestinationTable { get; set; }
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
    public ICollection<MigrationExecutionHistory> Executions { get; set; } = new List<MigrationExecutionHistory>();

    // Auditing fields for tracking changes
    public string? ChangedBy { get; set; }
    public DateTime? ChangedOn { get; set; }

    // IMultiStepWorkflow implementation
    private List<QueryStepData>? _parsedSteps;
    private List<QueryStepData> ParsedSteps
    {
        get
        {
            if (_parsedSteps == null)
            {
                try
                {
                    _parsedSteps = JsonSerializer.Deserialize<List<QueryStepData>>(QueryText) ?? new List<QueryStepData>();
                }
                catch
                {
                    _parsedSteps = new List<QueryStepData>();
                }
            }
            return _parsedSteps;
        }
    }

    public bool IsMultiStep => ParsedSteps.Count > 1;
    public bool IsCrossDataSource => ParsedSteps.Select(s => s.DataSourceId).Distinct().Count() > 1;
    public bool IsCrossDatabase => ParsedSteps
        .Where(s => s.DatabaseEngineType.HasValue)
        .Select(s => s.DatabaseEngineType!.Value)
        .Distinct()
        .Count() > 1;
    public List<int> DataSourceIds => ParsedSteps.Select(s => s.DataSourceId).Distinct().ToList();
    public List<DatabaseEngineType> DatabaseEngines => ParsedSteps
        .Where(s => s.DatabaseEngineType.HasValue)
        .Select(s => s.DatabaseEngineType!.Value)
        .Distinct()
        .ToList();
}