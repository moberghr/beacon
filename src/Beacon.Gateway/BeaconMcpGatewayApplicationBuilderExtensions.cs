using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.UI;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using Beacon.MCP.Discovery;

namespace Beacon.Gateway;

public static class BeaconMcpGatewayApplicationBuilderExtensions
{
    /// <summary>
    /// The MCP gateway's part of the pipeline, before the host's <c>UseAuthentication()</c>: the security response headers
    /// on Beacon's routes, the RFC 9728 <c>resource_metadata</c> pointer on every 401 from <c>/beacon/mcp</c>, Beacon's
    /// bearer middleware on <c>/beacon/mcp</c> only — a bearer token authenticates nobody on any other route — and the Host
    /// and Origin check on Beacon's routes (<c>Beacon:Security</c>), which lets an OpenID Connect callback through.
    /// </summary>
    public static IApplicationBuilder UseBeaconMcpGateway(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseBeaconSecurityHeaders();
        app.UseMcpResourceMetadataChallenge();
        app.UseWhen(
            x => x.Request.Path.StartsWithSegments(McpDiscoveryPaths.McpPath, StringComparison.OrdinalIgnoreCase),
            x => x.UseBeaconJwtBearerAuthentication());
        app.UseBeaconOriginGuard();

        return app;
    }

    /// <summary>
    /// Maps <c>/beacon/mcp</c> for mapped callers with the Execute scope (decided by the <c>BeaconMcp</c> scheme, never by a
    /// host session), the anonymous discovery documents, and a 404 for any other path under <c>/beacon/mcp</c> or
    /// <c>/.well-known</c> that nothing else maps — an MCP client probing for a document the server does not serve must be
    /// told it is absent, not handed the host's SPA shell.
    /// </summary>
    public static IEndpointRouteBuilder MapBeaconMcpGateway(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMcp(McpDiscoveryPaths.McpPath)
            .RequireAuthorization(McpRoutePolicy);

        // The admin UI: Beacon's shell and the gateway's part of its REST API, behind the host's admin policy.
        if (endpoints.ServiceProvider.GetRequiredService<BeaconMcpGatewayOptions>().AdminConfiguration is { } admin)
        {
            var api = endpoints.MapGroup(string.Empty);
            api.MapBeaconApi();
            ((IEndpointConventionBuilder)api).Finally(x => BeaconGatewayApiProfile.Apply(x, admin.HostPolicy));
            endpoints.MapBeaconUi();
        }
        endpoints.MapMcpDiscovery();
        endpoints.MapFallback($"{McpDiscoveryPaths.McpPath}/{{**path}}", () => Results.NotFound())
            .AllowAnonymous();
        endpoints.MapFallback("/.well-known/{**path}", () => Results.NotFound())
            .AllowAnonymous();

        return endpoints;
    }

    /// <summary>A caller the bearer middleware mapped (an MCP credential, never a session) that holds the Execute scope.</summary>
    internal static AuthorizationPolicy McpRoutePolicy { get; } = new AuthorizationPolicyBuilder(BeaconMcpGatewayDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(x => BeaconScopes.IsScopedCaller(x.User) && BeaconScopes.SatisfiesExecuteScope(x.User))
        .Build();
}
