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
/// mapped MCP JWT caller) on a write-gated request must also hold the Execute scope (§1.4), which
/// <see cref="BeaconScopeEndpointFilter"/> already demands of it on every such request, whatever this filter's settings.
/// An endpoint marked with a functional permission (<see cref="BeaconFunctionalPermissionMetadata"/>) admits a caller
/// who holds that permission now and is not an Admin, with no Viewer/Editor role (functional roles carry none), unless the
/// authorization provider vetoes it (<see cref="IBeaconAuthorizationProvider.AllowsFunctionalPermissionAsync"/>); an
/// Admin, who holds every permission, still needs read or write permission from the provider, as on every other route.
/// Where the permission is the endpoint's requirement (<see cref="BeaconViewerAccessExtensions.RequireBeaconPermission{TBuilder}"/>),
/// anyone else is refused, whatever role claims the session was signed in with (an API key with its own message: keys hold
/// no functional permission); where it is an extra way in
/// (<see cref="BeaconViewerAccessExtensions.AdmitBeaconPermission{TBuilder}"/>), anyone else needs read or write
/// permission as usual. An endpoint marked as an Admin's (<see cref="BeaconViewerAccessExtensions.RequireBeaconAdmin{TBuilder}"/>)
/// refuses a caller who is not an Admin now, whatever role claims the session carries.
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
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null
            || endpoint?.Metadata.GetMetadata<BeaconPermissionCheckExemptMetadata>() != null)
        {
            return await next(context);
        }

        WarnWhenNoProviderIsConfigured(httpContext, configuration);

        var method = httpContext.Request.Method;
        if (endpoint?.Metadata.GetMetadata<BeaconAdminMetadata>() != null && !(await PermissionsAsync(httpContext)).IsAdmin)
        {
            // Method and route pattern only (§1.11).
            logger.LogWarning(
                "Permission denied for {Method} {RoutePattern}: Admin required",
                method,
                (endpoint as RouteEndpoint)?.RoutePattern.RawText);

            return Results.Problem(
                title: "Forbidden",
                detail: "An Admin is required for this operation.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var functional = endpoint?.Metadata.GetMetadata<BeaconFunctionalPermissionMetadata>();
        if (functional != null)
        {
            var permissions = await PermissionsAsync(httpContext);
            if (!permissions.IsAdmin && await AllowsHeldPermissionAsync(httpContext, permissions, functional.Permissions))
            {
                return await next(context);
            }

            if (functional.IsRequired && !permissions.IsAdmin)
            {
                // Method, route pattern and the permission only (§1.11).
                logger.LogWarning(
                    "Permission denied for {Method} {RoutePattern}: {Permissions} required",
                    method,
                    (endpoint as RouteEndpoint)?.RoutePattern.RawText,
                    string.Join("|", functional.Permissions));

                return Results.Problem(
                    title: "Forbidden",
                    detail: RequiredPermissionDetail(httpContext.User, functional.Permissions),
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        var isSafeMethod = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        var viewerAccess = endpoint?.Metadata.GetMetadata<BeaconViewerAccessMetadata>();
        var requiresWrite = !isSafeMethod && viewerAccess?.AppliesTo(context) != true;

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

    /// <summary>
    /// The refusal detail of a route that requires one of <paramref name="permissions"/>: an API key is told that keys hold
    /// no functional permission, anyone else which permission is required.
    /// </summary>
    internal static string RequiredPermissionDetail(System.Security.Claims.ClaimsPrincipal? user, IReadOnlyList<string> permissions)
    {
        var names = string.Join(" or ", permissions);

        return user != null && BeaconUserLookup.IsApiKey(user)
            ? $"API keys cannot use administrative routes; sign in as a user with the {names} permission."
            : $"The {names} permission is required for this operation.";
    }

    // The caller's live functional permissions; a host without the accessor registered grants none.
    private static async Task<BeaconPermissionSet> PermissionsAsync(HttpContext httpContext)
    {
        var accessor = httpContext.RequestServices.GetService<IBeaconPermissionAccessor>();
        if (accessor == null)
        {
            return BeaconPermissionSet.None;
        }

        return await accessor.GetCurrentAsync(httpContext.RequestAborted);
    }

    // A permission the route admits, held by the caller and not vetoed by the authorization provider.
    private static async Task<bool> AllowsHeldPermissionAsync(HttpContext httpContext, BeaconPermissionSet held, IReadOnlyList<string> permissions)
    {
        var authorization = httpContext.RequestServices.GetRequiredService<IBeaconAuthorizationProvider>();
        foreach (var permission in permissions.Where(held.Holds))
        {
            if (await authorization.AllowsFunctionalPermissionAsync(permission, httpContext.RequestAborted))
            {
                return true;
            }
        }

        return false;
    }

    private static Task<bool> HasReadAccessAsync(HttpContext httpContext)
    {
        var authorization = httpContext.RequestServices.GetRequiredService<IBeaconAuthorizationProvider>();

        return authorization.HasReadPermissionAsync(httpContext.RequestAborted);
    }

    private static async Task<bool> HasWriteAccessAsync(HttpContext httpContext)
    {
        // The scope is a ceiling on what a key may do, not a grant: a write-scoped key still needs a writer behind it.
        if (!BeaconScopes.SatisfiesExecuteScope(httpContext.User))
        {
            return false;
        }

        var authorization = httpContext.RequestServices.GetRequiredService<IBeaconAuthorizationProvider>();

        return await authorization.HasWritePermissionAsync(httpContext.RequestAborted);
    }
}

/// <summary>
/// Marks a mutating <c>/beacon/api</c> endpoint that a Viewer may call (read permission suffices). Applied only to the
/// endpoints the Viewer scope allows: running an existing query's previews and changing one's own password. A
/// condition narrows it per request (the bound arguments are available); a request it rejects needs write permission.
/// </summary>
internal sealed class BeaconViewerAccessMetadata
{
    public static readonly BeaconViewerAccessMetadata Instance = new(null);

    private readonly Func<EndpointFilterInvocationContext, bool>? _condition;

    private BeaconViewerAccessMetadata(Func<EndpointFilterInvocationContext, bool>? condition)
    {
        _condition = condition;
    }

    public static BeaconViewerAccessMetadata When(Func<EndpointFilterInvocationContext, bool> condition)
    {
        return new BeaconViewerAccessMetadata(condition);
    }

    public bool AppliesTo(EndpointFilterInvocationContext context)
    {
        return _condition?.Invoke(context) ?? true;
    }
}

/// <summary>
/// Marks a <c>/beacon/api</c> endpoint with the functional permissions (<see cref="BeaconPermissions"/>) that admit a
/// caller to it without a Viewer or Editor role. <see cref="IsRequired"/>: the permission is the endpoint's requirement
/// (its policy names it), so a caller without it is refused; otherwise it is an extra way in beside read or write
/// permission.
/// </summary>
internal sealed class BeaconFunctionalPermissionMetadata(IReadOnlyList<string> permissions, bool isRequired)
{
    public IReadOnlyList<string> Permissions { get; } = permissions;

    public bool IsRequired { get; } = isRequired;
}

/// <summary>
/// Marks a <c>/beacon/api</c> endpoint as an Admin's: besides the Admin-role policy, the permission filter admits only a
/// caller who is an Admin now (live roles), so a session signed in before its Admin role was removed is refused.
/// </summary>
internal sealed class BeaconAdminMetadata
{
    public static readonly BeaconAdminMetadata Instance = new();
}

/// <summary>
/// Marks an authenticated <c>/beacon/api</c> endpoint the permission filter does not gate, because it only reports the
/// caller's own state (e.g. <c>auth/permissions</c>, which must answer a user with no role instead of refusing them).
/// </summary>
internal sealed class BeaconPermissionCheckExemptMetadata
{
    public static readonly BeaconPermissionCheckExemptMetadata Instance = new();
}

internal static class BeaconViewerAccessExtensions
{
    /// <summary>
    /// Requires the functional-permission policy <paramref name="policy"/> (<see cref="BeaconPermissions.Policies"/>): the
    /// Admin role or a mapped role claim on the route, and the permission held now in the permission filter.
    /// </summary>
    public static TBuilder RequireBeaconPermission<TBuilder>(this TBuilder builder, string policy)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder
            .RequireAuthorization(policy)
            .WithMetadata(new BeaconFunctionalPermissionMetadata(BeaconPermissions.Policies[policy], isRequired: true));
    }

    /// <summary>
    /// Requires the Admin-role policy on the route and, in the permission filter, a caller who is an Admin now.
    /// </summary>
    public static TBuilder RequireBeaconAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName)
            .WithMetadata(BeaconAdminMetadata.Instance);
    }

    /// <summary>Also admits a holder of any of <paramref name="permissions"/>, who needs no Viewer or Editor role here.</summary>
    public static TBuilder AdmitBeaconPermission<TBuilder>(this TBuilder builder, params string[] permissions)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.WithMetadata(new BeaconFunctionalPermissionMetadata(permissions, isRequired: false));
    }

    /// <summary>Exempts this authenticated endpoint from the Viewer/Editor permission filter.</summary>
    public static TBuilder SkipBeaconPermissionCheck<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.WithMetadata(BeaconPermissionCheckExemptMetadata.Instance);
    }

    /// <summary>Lets callers with read permission (Viewers) reach this mutating endpoint.</summary>
    public static TBuilder AllowViewerAccess<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.WithMetadata(BeaconViewerAccessMetadata.Instance);
    }

    /// <summary>Lets Viewers reach this mutating endpoint only for requests matching <paramref name="condition"/>.</summary>
    public static TBuilder AllowViewerAccess<TBuilder>(
        this TBuilder builder,
        Func<EndpointFilterInvocationContext, bool> condition)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.WithMetadata(BeaconViewerAccessMetadata.When(condition));
    }
}
