using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Endpoints;

public static class BeaconApiEndpoints
{
    public const string AuthPolicyName = "BeaconApi";
    public const string AdminPolicyName = "BeaconApiAdmin";

    /// <summary>
    /// Requires the <c>Execute</c> (or <c>Admin</c>) scope for API-key callers (§1.4).
    /// Interactive cookie/OIDC sessions carry no <c>scope</c> claim and are not scope-gated —
    /// they are already a full authenticated session governed by role. Only the scoped
    /// API-key identity minted by <c>ApiKeyAuthMiddleware</c> is constrained here.
    /// </summary>
    public const string ExecuteScopePolicyName = "BeaconApiExecute";

    private const string ApiKeyAuthMethod = "api_key";

    private static readonly string[] WriteScopes = ["Execute", "Admin"];

    public static IServiceCollection AddBeaconApiAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            // Scheme-agnostic on purpose: ApiKeyAuthMiddleware assigns context.User directly
            // (no scheme registration), so pinning the policy to a single scheme would 403
            // every API-key caller. RequireAuthenticatedUser() honours whatever identity the
            // upstream middleware (cookie OR API key) has put on the context.
            options.AddPolicy(AuthPolicyName, new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

            options.AddPolicy(AdminPolicyName, new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole("Admin")
                .Build());

            // §1.4 — API keys carry scopes; enforce the write scope on mutating endpoints.
            // A non-API-key (cookie/OIDC) caller has no auth_method=api_key claim and is
            // therefore allowed through; an API-key caller must present Execute or Admin.
            options.AddPolicy(ExecuteScopePolicyName, new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                {
                    var isApiKey = context.User.HasClaim("auth_method", ApiKeyAuthMethod);
                    if (!isApiKey)
                    {
                        return true;
                    }

                    return context.User
                        .FindAll("scope")
                        .Any(x => WriteScopes.Contains(x.Value));
                })
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
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithOpenApi();

        group.MapHealthEndpoints();
        group.MapAuthEndpoints();
        group.MapHomeEndpoints();
        group.MapAntiforgeryEndpoints();
        group.MapProjectsEndpoints();
        group.MapQueryFoldersEndpoints();
        group.MapQueriesEndpoints();
        group.MapQueryVersionsEndpoints();
        group.MapApprovalsEndpoints();
        group.MapApiKeysEndpoints();
        group.MapDashboardsEndpoints();
        group.MapDataQualityEndpoints();
        group.MapMcpManagementEndpoints();
        group.MapEvalEndpoints();
        group.MapGlossaryEndpoints();
        group.MapAiActorsEndpoints();
        group.MapNotificationsEndpoints();
        group.MapControlTowerEndpoints();
        group.MapMigrationsEndpoints();
        group.MapRecipientsEndpoints();
        group.MapSubscriptionsEndpoints();
        group.MapDataSourcesEndpoints();
        group.MapNotificationActionEndpoints();
        group.MapAdminSettingsEndpoints();
        group.MapUserSettingsEndpoints();
        group.MapTasksEndpoints();
        group.MapUsersEndpoints();

        return endpoints;
    }
}
