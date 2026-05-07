using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace Beacon.SampleProject.Endpoints;

internal static class BeaconApiEndpoints
{
    public const string AuthPolicyName = "BeaconApi";

    public static IServiceCollection AddBeaconApiAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicyName, new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build());
        });

        return services;
    }

    public static IEndpointRouteBuilder MapBeaconApi(this IEndpointRouteBuilder endpoints)
    {
        // Group-level auth policy: every endpoint requires an authenticated cookie session.
        // Endpoints opt out via .AllowAnonymous() (health, auth/me, csrf).
        var group = endpoints.MapGroup("/beacon/api")
            .RequireAuthorization(AuthPolicyName)
            .WithOpenApi();

        group.MapHealthEndpoints();
        group.MapAuthEndpoints();
        group.MapAntiforgeryEndpoints();
        group.MapProjectsEndpoints();
        group.MapQueryFoldersEndpoints();
        group.MapQueriesEndpoints();
        group.MapQueryVersionsEndpoints();
        group.MapApprovalsEndpoints();
        group.MapApiKeysEndpoints();
        group.MapDashboardsEndpoints();

        return endpoints;
    }
}
