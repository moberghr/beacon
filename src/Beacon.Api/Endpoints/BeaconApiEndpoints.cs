using Beacon.Api.Authentication;
using Beacon.Api.Hubs;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Endpoints;

public static class BeaconApiEndpoints
{
    public const string AuthPolicyName = "BeaconApi";
    public const string AdminPolicyName = "BeaconApiAdmin";

    /// <summary>
    /// Requires the <c>Execute</c> scope for scoped callers (§1.4): API keys minted by <c>ApiKeyAuthMiddleware</c> and
    /// JWT callers on <c>/beacon/mcp</c> minted by <c>JwtBearerAuthMiddleware</c> (<c>auth_method=mcp_caller</c>, scope
    /// decided by <see cref="IMcpCallerMapper"/>, never by the token). Interactive cookie/OIDC sessions and JWT callers
    /// on other routes carry no such marker and are not scope-gated — they are a full authenticated session governed
    /// by role. Every <c>/beacon/api</c> request that is not GET/HEAD/OPTIONS needs the same scope through
    /// <see cref="BeaconScopeEndpointFilter"/>, whether or not the endpoint names this policy.
    /// </summary>
    public const string ExecuteScopePolicyName = BeaconScopes.ExecuteScopePolicyName;

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

            // §1.4 — API keys and mapped MCP JWT callers carry scopes; enforce the Execute scope.
            // A cookie/OIDC caller has neither auth_method marker and is allowed through; a scoped
            // caller must present Execute. An unmapped MCP JWT caller has no scope → 403.
            options.AddPolicy(ExecuteScopePolicyName, new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(x => BeaconScopes.SatisfiesExecuteScope(x.User))
                .Build());
        });

        return services;
    }

    public static IEndpointRouteBuilder MapBeaconApi(this IEndpointRouteBuilder endpoints)
    {
        // Group-level auth policy: every endpoint requires an authenticated cookie session.
        // Endpoints opt out via .AllowAnonymous() (health, auth/me, csrf).
        // The scope filter always runs first: a scoped caller (API key, mapped MCP caller) needs the Execute scope for
        // every request that is not GET/HEAD/OPTIONS and for GETs marked RequiresExecuteScope().
        // The permission filter enforces Viewer (read) vs Editor (write) when authorization or user
        // management is on; it runs after antiforgery so a forged request never reaches the user lookup.
        var group = endpoints.MapGroup("/beacon/api")
            .RequireAuthorization(AuthPolicyName)
            .AddEndpointFilter<BeaconScopeEndpointFilter>()
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .AddEndpointFilter<BeaconPermissionEndpointFilter>()
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
        group.MapSchemaRelationshipsEndpoints();
        group.MapNotificationActionEndpoints();
        group.MapAdminSettingsEndpoints();
        group.MapUserSettingsEndpoints();
        group.MapTasksEndpoints();
        group.MapUsersEndpoints();

        // Mapped on `endpoints`, NOT on the group: the group carries the antiforgery filter,
        // which would reject the hub's negotiate handshake. Route and policy are the contract
        // the React shell and the published docs depend on — do not change them.
        // No registered options means the host never called AddBeaconApiServices(). Published
        // versions of this package documented MapBeaconApi() on its own, and such a host booted
        // fine with no hub — so treat "no options" as "no realtime" rather than throwing or
        // assuming realtime-on. Assuming on would call MapHub without AddSignalR and crash those
        // hosts at startup on a package bump. /auth/me reports realtimeEnabled=false in the same
        // case, so the server never advertises a hub it did not map.
        var options = endpoints.ServiceProvider.GetService<BeaconApiOptions>();
        if (options?.Realtime == true)
        {
            endpoints.MapHub<BeaconHub>("/beacon/api/hub").RequireAuthorization(AuthPolicyName);
        }

        return endpoints;
    }
}
