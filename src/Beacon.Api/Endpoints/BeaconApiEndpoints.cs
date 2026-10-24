using System.Security.Claims;
using Beacon.Api.Authentication;
using Beacon.Api.Hubs;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Endpoints;

public static class BeaconApiEndpoints
{
    public const string AuthPolicyName = "BeaconApi";

    internal const string GroupPrefix = "/beacon/api";
    public const string AdminPolicyName = "BeaconApiAdmin";

    /// <summary>The realtime hub's route (negotiate and connect).</summary>
    internal const string HubPath = "/beacon/api/hub";

    /// <summary>
    /// Requires the <c>Execute</c> scope for scoped callers (§1.4): API keys minted by <c>ApiKeyAuthMiddleware</c> and
    /// JWT callers on <c>/beacon/mcp</c> minted by <c>JwtBearerAuthMiddleware</c> (<c>auth_method=mcp_caller</c>, scope
    /// decided by <see cref="IMcpCallerMapper"/>, never by the token). Interactive cookie/OIDC sessions and JWT callers
    /// on other routes carry no such marker and are not scope-gated — they are a full authenticated session governed
    /// by role. Every <c>/beacon/api</c> request that is not GET/HEAD/OPTIONS needs the same scope through
    /// <see cref="BeaconScopeEndpointFilter"/>, whether or not the endpoint names this policy.
    /// </summary>
    public const string ExecuteScopePolicyName = BeaconScopes.ExecuteScopePolicyName;

    /// <summary>
    /// The realtime hub's admission (<see cref="BeaconHubAccess"/>), required next to <see cref="AuthPolicyName"/>: a
    /// caller without read permission and without a functional permission whose pages use hub events is refused (403).
    /// Decided at the hub's negotiate and connect requests, where the resource is the request. A policy object rather than
    /// a registered name, so a host mapping the hub needs no further registration.
    /// </summary>
    internal static AuthorizationPolicy HubAccessPolicy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(async x => x.Resource is HttpContext httpContext && await BeaconHubAccess.AllowsAsync(httpContext))
        .Build();

    public static IServiceCollection AddBeaconApiAuthorization(this IServiceCollection services)
    {
        // The per-caller limits of /beacon/api and of the anonymous requests Beacon refuses (Beacon:RateLimits), here as
        // well as in AddBeaconApiServices: every host that maps Beacon's routes registers its policies.
        services.TryAddSingleton<BeaconApiRateLimiter>();

        services.AddAuthorization(options =>
        {
            // Scheme-agnostic on purpose: ApiKeyAuthMiddleware assigns context.User directly
            // (no scheme registration), so pinning the policy to a single scheme would 403
            // every API-key caller. RequireAuthenticatedUser() honours whatever identity the
            // upstream middleware (cookie OR API key) has put on the context.
            options.AddPolicy(AuthPolicyName, new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

            // The caller's Admin standing as Beacon decides it (BeaconActor.IsAdmin), not a bare role claim: while Beacon
            // manages users, a host sign-in's own Admin role claim opens no Admin route.
            options.AddPolicy(AdminPolicyName, new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(IsBeaconAdminAsync)
                .Build());

            // §1.4 — API keys and mapped MCP JWT callers carry scopes; enforce the Execute scope.
            // A cookie/OIDC caller has neither auth_method marker and is allowed through; a scoped
            // caller must present Execute. An unmapped MCP JWT caller has no scope → 403.
            options.AddPolicy(ExecuteScopePolicyName, new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(x => BeaconScopes.SatisfiesExecuteScope(x.User))
                .Build());
        });

        // One policy per functional permission (and per pair a route admits): the Admin role, or the role each permission
        // is mapped to (Beacon:Authorization:Roles, names compared ignoring case), as the session's role claims carry them.
        // On a host without Beacon user management the mapped roles count only with Roles:HonourHostRoleClaims, as in the
        // permission accessor; there the Admin role alone admits. An API key carries no role.
        services.AddOptions<AuthorizationOptions>()
            .Configure<IOptions<RoleMappingOptions>, IServiceProvider>((options, roleMapping, serviceProvider) =>
            {
                var mapping = roleMapping.Value;
                // Without Beacon's configuration registered (Beacon itself not added) there is no host setting to follow.
                var userManagement = serviceProvider.GetService<Beacon.Core.BeaconConfiguration>()?.UserManagement.Enabled ?? true;
                var honoursRoleClaims = userManagement || mapping.Roles.HonourHostRoleClaims;
                foreach (var (policy, permissions) in BeaconPermissions.Policies)
                {
                    var roles = honoursRoleClaims
                        ? permissions
                            .Select(mapping.RoleNameFor)
                            .ToArray()
                        : [];
                    options.AddPolicy(policy, new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .RequireAssertion(async x => await IsBeaconAdminAsync(x) || HoldsMappedRole(x.User, roles))
                        .Build());
                }
            });

        // Fails startup when Core would read no request's caller (its off-request principal is still registered).
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, RequestPrincipalStartupCheck>());

        // Notes the active permission mapping and separation of duties at startup.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, BeaconPermissionStartupNote>());

        // Warns at startup about unlocked MCP safety settings and data sources whose engine does not refuse writes.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, ReadOnlyStartupWarnings>());

        // Warns at startup, outside Development, when data-source connections may skip server authentication.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, DataSourceTransportStartupWarning>());

        // Warns at startup about stored values still encrypted with a previous encryption key.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, EncryptionKeyStartupWarnings>());

        // Notes at startup how query changes are governed (approval workflow off, self-approval, strict runs).
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, ApprovalWorkflowStartupNote>());

        // Warns at startup about grounding no project admin curates (golden cases without a recorded promoter, learning
        // projects without a ProjectAdmin).
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, GroundingStartupNote>());

        // Notes at startup which key seals security audit events (a Warning when it is derived) and how long they are kept.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, SecurityAuditStartupNote>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, RetentionStartupNote>());

        // Applies the stored Admin settings (base URL, LLM provider configuration) once the host has started.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, StoredSettingsStartupApplier>());

        return services;
    }

    // The request's actor is an Admin (BeaconActor.IsAdmin: the stored user's live roles while Beacon manages users, the
    // host's Admin role claim without user management). Without a request or without Beacon's core registered there is no
    // actor to ask, and only the Admin role claim can answer.
    private static async Task<bool> IsBeaconAdminAsync(AuthorizationHandlerContext context)
    {
        if (context.Resource is not HttpContext httpContext
            || httpContext.RequestServices.GetService<IBeaconActorAccessor>() is not { } accessor)
        {
            return context.User.IsInRole(RoleService.RoleNames.Admin);
        }

        var actor = await accessor.GetCurrentAsync(httpContext.RequestAborted);

        return actor.IsAdmin;
    }

    // One of the mapped roles, ignoring case. The permission filter re-checks the permission live.
    private static bool HoldsMappedRole(ClaimsPrincipal user, IReadOnlyCollection<string> roles)
    {
        return roles.Count > 0
            && user.Identities
                .Any(x => x
                    .FindAll(x.RoleClaimType)
                    .Any(y => roles.Contains(y.Value, StringComparer.OrdinalIgnoreCase)));
    }

    public static IEndpointRouteBuilder MapBeaconApi(this IEndpointRouteBuilder endpoints)
    {
        // Group-level auth policy: every endpoint requires an authenticated cookie session.
        // Endpoints opt out via .AllowAnonymous() (health, auth/me, csrf).
        // The rate-limit filter is outermost: a caller over its Beacon:RateLimits:Api limit gets 429 before anything runs.
        // The access-exception filter is next: it maps refused and missing resources to 403/404 problems.
        // The scope filter runs next: a scoped caller (API key, mapped MCP caller) needs the Execute scope for
        // every request that is not GET/HEAD/OPTIONS and for GETs marked RequiresExecuteScope().
        // The permission filter enforces Viewer (read) vs Editor (write) when authorization or user
        // management is on; it runs after antiforgery so a forged request never reaches the user lookup.
        var group = endpoints.MapGroup(GroupPrefix)
            .RequireAuthorization(AuthPolicyName)
            .AddEndpointFilter<BeaconApiRateLimitFilter>()
            .AddEndpointFilter<BeaconAccessExceptionEndpointFilter>()
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
        group.MapDataQualityEndpoints();
        group.MapMcpManagementEndpoints();
        group.MapEvalEndpoints();
        group.MapGlossaryEndpoints();
        group.MapAiActorsEndpoints();
        group.MapNotificationsEndpoints();
        group.MapControlTowerEndpoints();

        // Data migration is the one feature that writes to a connected database: its endpoints exist only on a host that
        // turned it on (Beacon:Migrations:Enabled). Unmapped, every migration route answers 404.
        if (endpoints.ServiceProvider.GetService<IOptions<MigrationOptions>>()?.Value.Enabled == true)
        {
            group.MapMigrationsEndpoints();
        }

        // Embedding saved queries is off unless the host turned it on (Beacon:Embed:Enabled): unmapped, every embed route
        // answers 404.
        if (endpoints.ServiceProvider.GetService<IOptions<EmbedOptions>>()?.Value.Enabled == true)
        {
            group.MapEmbedGrantsEndpoints();
        }

        group.MapRecipientsEndpoints();
        group.MapSubscriptionsEndpoints();
        group.MapDataSourcesEndpoints();
        group.MapSchemaRelationshipsEndpoints();
        group.MapNotificationActionEndpoints();
        group.MapAdminSettingsEndpoints();
        group.MapUserSettingsEndpoints();
        group.MapTasksEndpoints();
        group.MapUsersEndpoints();
        group.MapSecurityAuditEndpoints();

        // Mapped on `endpoints`, NOT on the group: the group carries the antiforgery filter,
        // which would reject the hub's negotiate handshake. Route and policy are the contract
        // the React shell and the published docs depend on — do not change them.
        // No registered options means the host never called AddBeaconApiServices(). Published
        // versions of this package documented MapBeaconApi() on its own, and such a host booted
        // fine with no hub — so treat "no options" as "no realtime" rather than throwing or
        // assuming realtime-on. Assuming on would call MapHub without AddSignalR and crash those
        // hosts at startup on a package bump. /auth/me reports realtimeEnabled=false in the same
        // case, so the server never advertises a hub it did not map.
        // The hub also admits only callers who may use it (HubAccessPolicy), and a connection is closed when the
        // expiry its request reported passes: for a cookie session the session's next check against the user store
        // (BeaconSessionEvents), capped at its absolute lifetime; for an API key the same interval (ApiKeyAuthMiddleware).
        // The client then reconnects, and is checked again.
        var options = endpoints.ServiceProvider.GetService<BeaconApiOptions>();
        if (options?.Realtime == true)
        {
            endpoints.MapHub<BeaconHub>(HubPath, x => x.CloseOnAuthenticationExpiration = true)
                .RequireAuthorization(AuthPolicyName)
                .RequireAuthorization(HubAccessPolicy);
        }

        return endpoints;
    }
}
