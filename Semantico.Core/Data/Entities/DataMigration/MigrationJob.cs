using System.Text.Json;
using Semantico.Core.Abstractions;
using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Queries;

namespace Semantico.Core.Data.Entities.DataMigration;

public class MigrationJob : ArchivableBaseEntity, IScheduledJob, IMultiStepWorkflow
{
    public required string Name { get; set; }
    public required string Description { get; set; }
    
    // Query Configuration
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public required string QueryText { get; set; }
    
    // Destination Configuration  
    public int DestinationProjectId { get; set; }
    public Project DestinationProject { get; set; } = null!;
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
    public bool IsCrossProject => ParsedSteps.Select(s => s.ProjectId).Distinct().Count() > 1;
    public bool IsCrossDatabase => ParsedSteps.Select(s => s.DatabaseEngineType).Distinct().Count() > 1;
    public List<int> ProjectIds => ParsedSteps.Select(s => s.ProjectId).Distinct().ToList();
    public List<DatabaseEngineType> DatabaseEngines => ParsedSteps.Select(s => s.DatabaseEngineType).Distinct().ToList();
}