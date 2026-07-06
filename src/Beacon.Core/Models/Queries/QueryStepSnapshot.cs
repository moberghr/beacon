using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Queries;

public class QueryStepSnapshot
{
    public int StepOrder { get; set; }
    public string SqlValue { get; set; } = null!;
    public int DataSourceId { get; set; }
    public string DataSourceName { get; set; } = null!;
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<QueryStepParameterSnapshot> Parameters { get; set; } = [];
}

public class QueryStepParameterSnapshot
{
    public string Name { get; set; } = null!;
    public ParameterType Type { get; set; }
    public string? Description { get; set; }
    public string? Placeholder { get; set; }
}
