using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class QueryStepParameter : BaseEntity
{
    public required int QueryStepId { get; set; }

    public QueryStep QueryStep { get; set; } = null!;

    public required string Name { get; set; }

    public required ParameterType Type { get; set; }

    public string? Description { get; set; }

    public string? Placeholder { get; set; }
}