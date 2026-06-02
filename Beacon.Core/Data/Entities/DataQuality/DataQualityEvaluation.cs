using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.DataQuality;

public class DataQualityEvaluation : BaseEntity
{
    public int DataContractId { get; set; }

    public double OverallScore { get; set; }

    public int PassedRules { get; set; }

    public int FailedRules { get; set; }

    public int TotalRules { get; set; }

    public double ExecutionTimeMs { get; set; }

    public List<DataQualityRuleResult> RuleResults { get; set; } = new();

    public DataContract DataContract { get; set; } = null!;
}
