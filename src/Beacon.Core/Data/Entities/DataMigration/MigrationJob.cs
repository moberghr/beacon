using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities.DataMigration;

public class MigrationJob : ArchivableBaseEntity, IAttributedEntity
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

    /// <summary>
    /// While the change approval workflow governs the job: the hash of the definition a second person approved
    /// (<see cref="ChangeApprovals.MigrationJobDefinition"/>), and the job runs only while its definition hashes to it;
    /// <see cref="ChangeApprovals.ChangeApprovalHashes.NothingApproved"/> for a job created under the workflow and not
    /// approved yet. Null for a job the workflow does not govern (stored before Beacon reviewed migration jobs, or last
    /// saved with the workflow off), which runs as before.
    /// </summary>
    public string? ApprovedDefinitionHash { get; set; }

    /// <inheritdoc />
    public string? CreatedByUserId { get; set; }

    /// <inheritdoc />
    public string? ModifiedByUserId { get; set; }

    /// <inheritdoc />
    public DateTime? ModifiedTime { get; set; }
}
