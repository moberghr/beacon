namespace Beacon.Connector.Api.Models;

public class ApiConnectionConfig
{
    public required string BaseUrl { get; set; }
    public required string OpenApiSpecUrl { get; set; }
    public ApiAuthConfig? Auth { get; set; }
    public ApiEndpointFilter? EndpointFilter { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
