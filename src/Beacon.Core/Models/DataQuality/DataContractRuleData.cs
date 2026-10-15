using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.DataQuality;

public record DataContractRuleData
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public DataContractRuleType RuleType { get; init; }
    public string? ColumnName { get; init; }
    public string Configuration { get; init; } = null!;
    public DataContractSeverity Severity { get; init; }
    public double Weight { get; init; }
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Read only (ignored when saving): while the approval workflow covers Custom SQL rules, this Custom SQL rule's SQL
    /// waits for approval (new, or its SQL or data source changed, or its change was rejected), so evaluations leave it out.
    /// </summary>
    public bool AwaitingApproval { get; init; }
}
