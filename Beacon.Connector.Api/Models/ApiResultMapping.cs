namespace Beacon.Connector.Api.Models;

public class ApiResultMapping
{
    public required string ArrayPath { get; set; } // JSONPath to the array, e.g. "$.data"
    public List<ApiColumnMapping>? Columns { get; set; } // null = auto-detect from first element
}

public class ApiColumnMapping
{
    public required string Name { get; set; }
    public required string Path { get; set; } // JSONPath relative to each array element
    public string Type { get; set; } = "string";
}
