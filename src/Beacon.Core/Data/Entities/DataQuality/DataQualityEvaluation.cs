using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities.DataQuality;

public class DataQualityEvaluation : BaseEntity
{
    public int DataContractId { get; set; }

    public double OverallScore { get; set; }

    public int PassedRules { get; set; }

    public int FailedRules { get; set; }

    public int TotalRules { get; set; }

    /// <summary>
    /// The enabled Custom SQL rules this evaluation left out because their SQL waited for approval: more than none makes
    /// its score partial. Zero for evaluations made before Beacon reviewed Custom SQL rules.
    /// </summary>
    public int AwaitingApprovalRules { get; set; }

    /// <summary>The alert recipients this evaluation's alert skipped because where they deliver waited for approval.</summary>
    public int HeldAlertRecipients { get; set; }

    public double ExecutionTimeMs { get; set; }

    public List<DataQualityRuleResult> RuleResults { get; set; } = new();

    public DataContract DataContract { get; set; } = null!;
}
