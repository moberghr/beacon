using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities.DataQuality;

public class DataContract : ArchivableBaseEntity
{
    public int DataSourceId { get; set; }

    public required string SchemaName { get; set; }

    public required string TableName { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string CronExpression { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string? OwnerUserId { get; set; }

    public bool AlertOnFailure { get; set; } = true;

    public int FailureThresholdScore { get; set; } = 80;

    public List<DataContractRule> Rules { get; set; } = new();

    public List<DataQualityEvaluation> Evaluations { get; set; } = new();

    public DataSource DataSource { get; set; } = null!;
}
