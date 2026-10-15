using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.DataQuality;

public class DataContract : ArchivableBaseEntity, IAttributedEntity
{
    public List<Recipient> Recipients { get; set; } = new();

    public int DataSourceId { get; set; }

    public required string SchemaName { get; set; }

    public required string TableName { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string CronExpression { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string? OwnerUserId { get; set; }

    /// <inheritdoc />
    public string? CreatedByUserId { get; set; }

    /// <inheritdoc />
    public string? ModifiedByUserId { get; set; }

    /// <inheritdoc />
    public DateTime? ModifiedTime { get; set; }

    /// <summary>
    /// When a scheduled evaluation was first skipped because the owner may no longer run the contract's rules; null once
    /// an evaluation runs again, or when none was skipped.
    /// </summary>
    public DateTime? OwnerNotAuthorizedSince { get; set; }

    public bool AlertOnFailure { get; set; } = true;

    public int FailureThresholdScore { get; set; } = 80;

    public List<DataContractRule> Rules { get; set; } = new();

    public List<DataQualityEvaluation> Evaluations { get; set; } = new();

    public DataSource DataSource { get; set; } = null!;
}
