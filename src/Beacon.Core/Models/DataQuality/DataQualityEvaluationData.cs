namespace Beacon.Core.Models.DataQuality;

public record DataQualityEvaluationData
{
    public int Id { get; init; }
    public int DataContractId { get; init; }
    public double OverallScore { get; init; }
    public int PassedRules { get; init; }
    public int FailedRules { get; init; }
    public int TotalRules { get; init; }

    /// <summary>
    /// The enabled Custom SQL rules left out because their SQL waits for approval (while the approval workflow covers
    /// them). More than none makes the score partial (<see cref="IsPartial"/>).
    /// </summary>
    public int AwaitingApprovalRules { get; init; }

    /// <summary>The score leaves out rules that wait for approval: it does not replace the table's score.</summary>
    public bool IsPartial => AwaitingApprovalRules > 0;

    /// <summary>The alert recipients this evaluation's alert skipped because where they deliver waits for approval.</summary>
    public int HeldAlertRecipients { get; init; }

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
