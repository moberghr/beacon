using Beacon.Api.Endpoints;
using Beacon.Core;
using System.Runtime.CompilerServices;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Microsoft.AspNetCore.Authorization;

namespace Beacon.Api.Authentication;

/// <summary>
/// Server-side Viewer/Editor enforcement for every endpoint in the <c>/beacon/api</c> group. Active only when
/// <c>Authorization.Enabled</c> or <c>UserManagement.Enabled</c> is set; anonymous endpoints are never checked.
/// Safe methods (GET/HEAD/OPTIONS) need read permission; any other method needs write permission unless the endpoint
/// is marked with <see cref="BeaconViewerAccessExtensions.AllowViewerAccess{TBuilder}"/>. A scoped caller (API key or
/// mapped MCP JWT caller) on a write-gated request must also hold the Execute or Admin scope (§1.4).
/// <para>
/// An endpoint filter, not middleware, so the load-bearing middleware order (§1.9) is untouched. Services are
/// resolved from the request scope so the provider (and its user lookup) is only built when enforcement is on.
/// </para>
/// </summary>
internal sealed class BeaconPermissionEndpointFilter(ILogger<BeaconPermissionEndpointFilter> logger) : IEndpointFilter
{
    // One entry per host (keyed by its singleton configuration): the filter is instantiated per endpoint, so an
    // instance flag would warn once per endpoint.
    private static readonly ConditionalWeakTable<BeaconConfiguration, object> ProviderChecked = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetRequiredService<BeaconConfiguration>();
        if (!configuration.Authorization.Enabled && !configuration.UserManagement.Enabled)
        {
            return await next(context);
        }

        var endpoint = httpContext.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            return await next(context);
        }

        WarnWhenNoProviderIsConfigured(httpContext, configuration);

        var method = httpContext.Request.Method;
        var isSafeMethod = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        var requiresWrite = !isSafeMethod && endpoint?.Metadata.GetMetadata<BeaconViewerAccessMetadata>() == null;

        var isAllowed = requiresWrite
            ? await HasWriteAccessAsync(httpContext)
            : await HasReadAccessAsync(httpContext);
        if (isAllowed)
        {
            return await next(context);
        }

        // Method + route pattern only (§1.11): the concrete path can carry ids, and the user is not logged.
        logger.LogWarning(
            "Permission denied for {Method} {RoutePattern}",
            method,
            (endpoint as RouteEndpoint)?.RoutePattern.RawText);

        return Results.Problem(
            title: "Forbidden",
            detail: requiresWrite
                ? "Write permission is required for this operation."
                : "Read permission is required for this operation.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    // Authorization.Enabled with no provider type and user management off resolves the allow-all default provider,
    // so enforcement would silently let every authenticated caller through. Logged once, no request data (§1.11).
    private void WarnWhenNoProviderIsConfigured(HttpContext httpContext, BeaconConfiguration configuration)
    {
        if (!ProviderChecked.TryAdd(configuration, new object()))
        {
            return;
        }

        var authorization = httpContext.RequestServices.GetRequiredService<IBeaconAuthorizationProvider>();
        if (authorization is DefaultAuthorizationProvider)
        {
            logger.LogWarning(
                "Beacon authorization is enabled but no authorization provider is configured; all authenticated callers are allowed.");
        }
    }

    private static Task<bool> HasReadAccessAsync(HttpContext httpContext)
    {
        var authorization = httpContext.RequestServices.GetRequiredService<IBeaconAuthorizationProvider>();

        return authorization.HasReadPermissionAsync(httpContext.RequestAborted);
    }

    private static async Task<bool> HasWriteAccessAsync(HttpContext httpContext)
    {
        // The scope is a ceiling on what a key may do, not a grant: a write-scoped key still needs a writer behind it.
        if (!BeaconApiEndpoints.SatisfiesExecuteScope(httpContext.User))
        {
            return false;
        }

        var authorization = httpContext.RequestServices.GetRequiredService<IBeaconAuthorizationProvider>();

        return await authorization.HasWritePermissionAsync(httpContext.RequestAborted);
    }
}

/// <summary>
/// Marks a mutating <c>/beacon/api</c> endpoint that a Viewer may call (read permission suffices). Applied only to the
/// endpoints the Viewer scope allows: running an existing query's previews and changing one's own password.
/// </summary>
internal sealed class BeaconViewerAccessMetadata
{
    public static readonly BeaconViewerAccessMetadata Instance = new();

    private BeaconViewerAccessMetadata()
    {
    }
}

internal static class BeaconViewerAccessExtensions
{
    /// <summary>Lets callers with read permission (Viewers) reach this mutating endpoint.</summary>
    public static TBuilder AllowViewerAccess<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.WithMetadata(BeaconViewerAccessMetadata.Instance);
    }
}
