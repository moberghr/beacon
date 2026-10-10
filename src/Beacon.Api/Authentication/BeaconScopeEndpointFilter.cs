using Beacon.Core.Authorization;
using Beacon.Core.Mcp;

namespace Beacon.Api.Authentication;

/// <summary>
/// Scope enforcement for every endpoint in the <c>/beacon/api</c> group (§1.4). A scoped caller (API key or mapped MCP
/// JWT caller) needs the Execute scope for every request that is not GET/HEAD/OPTIONS, and for a GET marked with
/// <see cref="BeaconScopeEndpointExtensions.RequiresExecuteScope{TBuilder}"/> (one that runs SQL, dials a data source,
/// makes an outbound call or calls the LLM); the Read scope is left with read-only GETs. Always on — unlike the
/// Viewer/Editor permission filter it does not depend on <c>Authorization.Enabled</c> or
/// <c>UserManagement.Enabled</c>. Callers without a scope marker (cookie/OIDC sessions, REST bearer callers) pass.
/// </summary>
internal sealed class BeaconScopeEndpointFilter(ILogger<BeaconScopeEndpointFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        if (!BeaconScopes.IsScopedCaller(httpContext.User) || BeaconScopes.SatisfiesExecuteScope(httpContext.User))
        {
            return await next(context);
        }

        var method = httpContext.Request.Method;
        var isSafeMethod = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        var endpoint = httpContext.GetEndpoint();
        if (isSafeMethod && endpoint?.Metadata.GetMetadata<RequiresExecuteScopeMetadata>() == null)
        {
            return await next(context);
        }

        // Method, route pattern and key id only (§1.11): the concrete path can carry ids; an API key is named by its id
        // (an MCP caller has none), never by its value.
        logger.LogWarning(
            "Execute scope required for {Method} {RoutePattern} ApiKeyId={ApiKeyId}",
            method,
            (endpoint as RouteEndpoint)?.RoutePattern.RawText,
            httpContext.User.FindFirst(McpCallerClaimTypes.ApiKeyId)?.Value);

        return Results.Problem(
            title: "Forbidden",
            detail: "This operation requires the Execute scope.",
            statusCode: StatusCodes.Status403Forbidden);
    }
}

/// <summary>
/// Marks a GET <c>/beacon/api</c> endpoint that a scoped caller may only call with the Execute scope, because it runs
/// SQL, dials a data source, makes an outbound call or calls the LLM.
/// </summary>
internal sealed class RequiresExecuteScopeMetadata
{
    public static readonly RequiresExecuteScopeMetadata Instance = new();
}

internal static class BeaconScopeEndpointExtensions
{
    /// <summary>Requires the Execute scope of a scoped caller on this read endpoint (see <see cref="BeaconScopeEndpointFilter"/>).</summary>
    public static TBuilder RequiresExecuteScope<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.WithMetadata(RequiresExecuteScopeMetadata.Instance);
    }
}
