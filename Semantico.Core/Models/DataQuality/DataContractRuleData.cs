using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.DataQuality;

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
}
