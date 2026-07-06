namespace Beacon.Connector.Api.Models;

public class ApiQueryDefinition
{
    public required string Method { get; set; }
    public required string Path { get; set; }
    public ApiQueryParameters? Parameters { get; set; }
    public string? Body { get; set; }
    public required ApiResultMapping ResultMapping { get; set; }
}

public class ApiQueryParameters
{
    public Dictionary<string, string> Query { get; set; } = new();
    public Dictionary<string, string> Header { get; set; } = new();
    public Dictionary<string, string> Path { get; set; } = new();
}
