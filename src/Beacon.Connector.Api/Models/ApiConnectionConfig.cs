namespace Beacon.Connector.Api.Models;

public class ApiConnectionConfig
{
    public required string BaseUrl { get; set; }
    public required string OpenApiSpecUrl { get; set; }
    public ApiAuthConfig? Auth { get; set; }
    public ApiEndpointFilter? EndpointFilter { get; set; }
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Opt-in for POST queries against search-style endpoints. When false (default)
    /// only read-only verbs (GET/HEAD/OPTIONS) are permitted; mutating verbs are rejected.
    /// </summary>
    public bool AllowPostQueries { get; set; }
}
