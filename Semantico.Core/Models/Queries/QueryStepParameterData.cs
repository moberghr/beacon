using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Queries;

public class QueryStepParameterData
{
    public string Name { get; set; } = null!;
    
    public ParameterType Type { get; set; }
    
    public string? Description { get; set; }
    
    public string? Placeholder { get; set; }
}