using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Queries;

public class QueryStepParameterData
{
    public string Name { get; set; } = null!;

    public ParameterType Type { get; set; }

    public string? Description { get; set; }

    public string? Placeholder { get; set; }
}