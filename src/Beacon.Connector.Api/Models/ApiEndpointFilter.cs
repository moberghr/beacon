namespace Beacon.Connector.Api.Models;

public class ApiEndpointFilter
{
    public List<string> IncludePathPatterns { get; set; } = new();
    public List<string> ExcludePathPatterns { get; set; } = new();
}
