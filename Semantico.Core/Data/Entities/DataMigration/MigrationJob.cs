using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities.DataMigration;

internal class MigrationJob : ArchivableBaseEntity
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
    public ICollection<MigrationExecution> Executions { get; set; } = new List<MigrationExecution>();
    
    // Auditing fields for tracking changes
    public string? ChangedBy { get; set; }
    public DateTime? ChangedOn { get; set; }
}