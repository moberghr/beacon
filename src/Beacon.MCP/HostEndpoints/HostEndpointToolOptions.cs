namespace Beacon.MCP.HostEndpoints;

/// <summary>Options for <see cref="HostEndpointToolsServiceCollectionExtensions.AddHostEndpointTools"/>.</summary>
public sealed class HostEndpointToolOptions
{
    /// <summary>
    /// The Beacon project the tools belong to. Only callers authorized for this project see or call them; when the
    /// project does not exist yet, no tool is listed (fail closed). Required.
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>The largest response body Beacon accepts from an endpoint; past it the call fails. Default 1 MiB.</summary>
    public int MaxResponseBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Up to this many tools, each is its own MCP tool <c>api_&lt;name&gt;</c>. Above it, the tools are reached through
    /// <c>search_api</c> and <c>call_api</c> so a large host does not flood the agent's tool list. Default 20.
    /// </summary>
    public int NamedToolLimit { get; set; } = 20;

    /// <summary>How long one dispatched request may run. Default 30 seconds.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Authentication schemes an exposed endpoint's authorization policy may name. Beacon dispatches in-process as the
    /// principal <c>IMcpHostPrincipalFactory</c> builds for the MCP caller and never runs the named scheme's handler,
    /// so a policy that demands a specific scheme (a step-up or MFA scheme, a partner API scheme) would be satisfied
    /// without that scheme ever authenticating. An exposed endpoint whose combined policy names any scheme not listed
    /// here fails startup. List a scheme only when the MCP caller's principal is an acceptable stand-in for it.
    /// Default empty (matching is case-sensitive, as ASP.NET Core scheme names are).
    /// </summary>
    public List<string> TrustedAuthenticationSchemes { get; set; } = [];

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            throw new InvalidOperationException("AddHostEndpointTools: ProjectName is required.");
        }

        if (MaxResponseBytes <= 0)
        {
            throw new InvalidOperationException("AddHostEndpointTools: MaxResponseBytes must be positive.");
        }

        if (NamedToolLimit < 0)
        {
            throw new InvalidOperationException("AddHostEndpointTools: NamedToolLimit must not be negative.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("AddHostEndpointTools: RequestTimeout must be positive.");
        }
    }
}
