using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.DataQuality;

public class DataQualityRuleResult : BaseEntity
{
    public int DataQualityEvaluationId { get; set; }

    public int DataContractRuleId { get; set; }

    public bool Passed { get; set; }

    public double Score { get; set; }

    public string? ActualValue { get; set; }

    public string? ExpectedValue { get; set; }

    public string? Message { get; set; }

    public double ExecutionTimeMs { get; set; }

    public DataQualityEvaluation DataQualityEvaluation { get; set; } = null!;

    public DataContractRule DataContractRule { get; set; } = null!;
}
