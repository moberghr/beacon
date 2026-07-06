using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Queries;

public class QueryParameterData
{
    public required string Name { get; init; }

    public required ParameterType Type { get; init; }

    public required string Description { get; init; }

    public required string Placeholder { get; init; }
}