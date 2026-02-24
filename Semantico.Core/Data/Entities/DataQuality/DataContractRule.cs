using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities.DataQuality;

public class DataContractRule : BaseEntity
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

    public DataContract DataContract { get; set; } = null!;
}
