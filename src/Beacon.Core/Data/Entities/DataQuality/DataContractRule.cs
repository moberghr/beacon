using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities.DataQuality;

/// <summary>
/// One rule of a data contract. A save that replaces the contract's rules retires the ones it replaces (archives them,
/// <see cref="ArchivableBaseEntity.ArchivedTime"/>) instead of deleting them, so the results of past evaluations keep the
/// definition that produced them. Retired rules are left out of every read by the soft-delete filter, and are never
/// evaluated again.
/// </summary>
public class DataContractRule : ArchivableBaseEntity
{
    public int DataContractId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public DataContractRuleType RuleType { get; set; }

    public string? ColumnName { get; set; }

    public required string Configuration { get; set; }

    public DataContractSeverity Severity { get; set; } = DataContractSeverity.Medium;

    public double Weight { get; set; } = 1.0;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// For a Custom SQL rule the change approval workflow governs: the hash of the SQL (on the contract's data source) a
    /// second person approved (<see cref="ChangeApprovals.CustomSqlRuleDefinition"/>), and the rule is evaluated only while
    /// it hashes to it; <see cref="ChangeApprovals.ChangeApprovalHashes.NothingApproved"/> for a rule added or changed under
    /// the workflow and not approved yet. Null for every other rule, and for a Custom SQL rule the workflow does not govern
    /// (stored before Beacon reviewed them, or last saved with the workflow off), which runs as before.
    /// </summary>
    public string? ApprovedSqlHash { get; set; }

    public DataContract DataContract { get; set; } = null!;
}
