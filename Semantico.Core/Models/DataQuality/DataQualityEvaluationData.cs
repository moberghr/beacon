namespace Semantico.Core.Models.DataQuality;

public record DataQualityEvaluationData
{
    public int Id { get; init; }
    public int DataContractId { get; init; }
    public double OverallScore { get; init; }
    public int PassedRules { get; init; }
    public int FailedRules { get; init; }
    public int TotalRules { get; init; }
    public double ExecutionTimeMs { get; init; }
    public DateTime CreatedTime { get; init; }
    public List<DataQualityRuleResultData> RuleResults { get; init; } = new();
}

public record DataQualityRuleResultData
{
    public int Id { get; init; }
    public int DataContractRuleId { get; init; }
    public string RuleName { get; init; } = null!;
    public bool Passed { get; init; }
    public double Score { get; init; }
    public string? ActualValue { get; init; }
    public string? ExpectedValue { get; init; }
    public string? Message { get; init; }
    public double ExecutionTimeMs { get; init; }
}
