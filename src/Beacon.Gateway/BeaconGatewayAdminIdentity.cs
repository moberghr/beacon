using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Gateway;

/// <summary>
/// Makes a holder of the host's admin policy Beacon's administrator on the gateway's admin paths (<c>/beacon</c> but not
/// <c>/beacon/mcp</c>): after the host's own claims transformations, which it runs first, it adds a Beacon identity —
/// first, carrying the Beacon user <c>host:&lt;id&gt;</c> as its <c>NameIdentifier</c> — for a signed-in principal that
/// passes <see cref="BeaconMcpGatewayAdminOptions.HostPolicy"/>. Beacon then finds that user, and its live Admin role
/// decides; the host's own role claims never do (Beacon ignores them while it manages users). Everyone else, and every
/// other path, is left as the host's transformations made it.
/// </summary>
internal sealed class BeaconGatewayAdminClaimsTransformation(IServiceProvider services, IHttpContextAccessor httpContextAccessor) : IClaimsTransformation
{
    public const string AuthenticationType = "BeaconGatewayAdmin";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // Authentication resolves one transformation; the host's own ones run first, whatever the path.
        foreach (var hostTransformation in services.GetServices<IClaimsTransformation>().Where(x => x is not BeaconGatewayAdminClaimsTransformation))
        {
            principal = await hostTransformation.TransformAsync(principal);
        }

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext == null
            || !BeaconGatewayPaths.IsAdminPath(httpContext.Request.Path)
            || principal.Identity?.IsAuthenticated != true
            || BeaconScopes.IsScopedCaller(principal)
            || principal.Identities.Any(x => x.AuthenticationType == AuthenticationType))
        {
            return principal;
        }

        var admin = services.GetRequiredService<BeaconMcpGatewayOptions>().AdminConfiguration!;
        var authorization = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, httpContext, admin.HostPolicy);
        if (!authorization.Succeeded || admin.HostUserId!(principal) is not { Length: > 0 } hostUserId)
        {
            return principal;
        }

        var user = await services.GetRequiredService<BeaconGatewayAdminDirectory>().EnsureAsync(hostUserId, principal, httpContext.RequestAborted);
        if (user == null)
        {
            return principal;
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.ExternalId),
                new Claim(BeaconClaims.UserId, user.ExternalId),
                new Claim(BeaconClaims.UserName, user.UserName),
                new Claim(BeaconClaims.Role, BeaconGatewayAdminDirectory.AdminRole),
                new Claim(ClaimTypes.Role, BeaconGatewayAdminDirectory.AdminRole),
                new Claim("DisplayName", user.DisplayName),
                new Claim(ClaimTypes.Email, user.Email ?? string.Empty)
            ],
            AuthenticationType,
            BeaconClaims.UserName,
            ClaimTypes.Role);

        return new ClaimsPrincipal(principal.Identities.Prepend(identity));
    }
}

/// <summary>The Beacon user behind a gateway administrator.</summary>
internal sealed record BeaconGatewayAdmin(int BeaconUserId, string ExternalId, string UserName, string DisplayName, string? Email);

/// <summary>
/// Finds — creating on first use, with the Admin role — the Beacon user <c>host:&lt;id&gt;</c> of a host administrator,
/// through Beacon's own external-user path. Cached for a few minutes, because that path records a sign-in on every call
/// and the transformation runs on every admin request; the host policy itself is judged on every request. A Beacon user an
/// administrator disabled or archived in Beacon is refused: Beacon may narrow what the host grants, never widen it.
/// </summary>
internal sealed partial class BeaconGatewayAdminDirectory(
    IUserManagementService users,
    IMemoryCache cache,
    ILogger<BeaconGatewayAdminDirectory> logger)
{
    public const string IdentityProvider = "host";

    public const string AdminRole = "Admin";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static string ExternalIdFor(string hostUserId) => $"{IdentityProvider}:{hostUserId}";

    public async Task<BeaconGatewayAdmin?> EnsureAsync(string hostUserId, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var externalId = ExternalIdFor(hostUserId);
        var key = $"beacon:gateway-admin:{externalId}";
        if (cache.TryGetValue(key, out BeaconGatewayAdmin? cached) && cached != null)
        {
            return cached;
        }

        var email = principal.FindFirst(ClaimTypes.Email)?.Value ?? principal.FindFirst("email")?.Value;
        var displayName = principal.FindFirst("name")?.Value ?? principal.Identity?.Name ?? externalId;
        var userName = email ?? principal.FindFirst("preferred_username")?.Value ?? externalId;

        try
        {
            var user = await users.GetOrCreateExternalUserAsync(externalId, IdentityProvider, userName, email, displayName, AdminRole, cancellationToken);
            var admin = new BeaconGatewayAdmin(user.Id, externalId, user.UserName, user.DisplayName ?? displayName, user.Email);
            cache.Set(key, admin, BeaconCacheEntries.For(Lifetime));

            return admin;
        }
        catch (BeaconException ex)
        {
            // Disabled or archived in Beacon. By the Beacon-side id only: never the e-mail or a name.
            LogRefused(logger, externalId, ex.GetType().Name);

            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Beacon refused the gateway administrator {ExternalId} ({Reason}).")]
    private static partial void LogRefused(ILogger logger, string externalId, string reason);
}

/// <summary>
/// The gateway's claims transformation has to be the one authentication resolves — it runs the host's own ones first —
/// so a host that registers its own after <c>AddBeaconMcpGateway</c> would silently lose the admin UI's identity. Startup
/// refuses that, naming the fix.
/// </summary>
internal sealed class BeaconGatewayAdminStartupCheck(IServiceScopeFactory scopes) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        using var scope = scopes.CreateScope();
        var resolved = scope.ServiceProvider.GetService<IClaimsTransformation>();
        if (resolved is not BeaconGatewayAdminClaimsTransformation)
        {
            throw new InvalidOperationException(
                $"The Beacon MCP gateway's admin UI needs its claims transformation to be the one authentication uses, but {resolved?.GetType().Name ?? "none"} "
                + "is registered after it. Register your IClaimsTransformation before calling AddBeaconMcpGateway; the gateway runs it first.");
        }

        return next;
    }
}

/// <summary>The paths the gateway serves.</summary>
internal static class BeaconGatewayPaths
{
    public const string UiPath = "/beacon";

    public const string ApiPath = "/beacon/api";

    /// <summary>Beacon's admin UI and REST API: everything under <c>/beacon</c> but the MCP route.</summary>
    public static bool IsAdminPath(PathString path) =>
        path.StartsWithSegments(UiPath, StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments(McpDiscoveryPaths.McpPath, StringComparison.OrdinalIgnoreCase);
}
