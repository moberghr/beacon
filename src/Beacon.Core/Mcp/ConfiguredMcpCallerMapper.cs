using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Mcp;

/// <summary>
/// Default <see cref="IMcpCallerMapper"/>, driven by <c>Beacon:Mcp:Callers</c> (<see cref="McpCallerOptions"/>).
/// Expects raw Entra claim names. Resolution order:
/// <list type="number">
/// <item>A configured system whose <c>ObjectId</c> equals <c>oid</c> (service user or service principal, any token type).</item>
/// <item>A configured system whose <c>ClientId</c> equals <c>azp</c>/<c>appid</c>, for app-only tokens only. A delegated
/// user token also carries the calling app's <c>azp</c>, so matching it there would turn every user into that system.</item>
/// <item>User mode, for delegated tokens, when enabled and the role/group requirement holds.</item>
/// </list>
/// Anything else is unknown and gets <c>null</c> (no access).
/// </summary>
public sealed class ConfiguredMcpCallerMapper(
    IOptions<McpCallerOptions> options,
    IMemoryCache cache,
    McpCallerSubjectHasher subjectHasher,
    ILogger<ConfiguredMcpCallerMapper> logger,
    IUserManagementService? userManagementService = null) : IMcpCallerMapper
{
    // Short on purpose: a Beacon user disabled by an admin loses MCP access within this window.
    private static readonly TimeSpan ProvisionedUserCacheDuration = TimeSpan.FromMinutes(1);

    public async Task<McpCaller?> MapAsync(ClaimsPrincipal jwtPrincipal, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var tenantId = GetClaim(jwtPrincipal, "tid") ?? GetClaim(jwtPrincipal, "iss") ?? string.Empty;
        var objectId = GetClaim(jwtPrincipal, "oid");
        var clientId = GetClaim(jwtPrincipal, "azp") ?? GetClaim(jwtPrincipal, "appid");
        var subject = objectId ?? clientId ?? GetClaim(jwtPrincipal, "sub");
        if (string.IsNullOrEmpty(subject))
        {
            logger.LogDebug("MCP caller rejected: token has no oid, azp/appid or sub claim.");
            return null;
        }

        var subjectHash = subjectHasher.Hash(tenantId, subject);
        var isAppOnly = IsAppOnlyToken(jwtPrincipal);

        var system = FindSystem(settings.Systems, objectId, clientId, isAppOnly);
        if (system != null)
        {
            return new McpCaller(
                McpCallerKind.System,
                system.Name,
                subjectHash,
                BeaconUserId: null,
                system.ProjectIds.Distinct().ToList(),
                system.Scope,
                system.HostClaims
                    .Select(x => new Claim(x.Type, x.Value))
                    .ToList());
        }

        if (isAppOnly || !settings.Users.Enabled || string.IsNullOrEmpty(objectId))
        {
            logger.LogInformation("MCP caller {CallerHash} rejected: not a configured system and not an accepted user.", subjectHash);
            return null;
        }

        var roles = GetClaimValues(jwtPrincipal, "roles");
        var groups = GetClaimValues(jwtPrincipal, "groups");
        if (!IsUserAdmitted(settings.Users, roles, groups))
        {
            logger.LogInformation("MCP caller {CallerHash} rejected: user lacks the required role or group.", subjectHash);
            return null;
        }

        int? beaconUserId = null;
        if (settings.Users.AutoProvision)
        {
            var provisioning = await ProvisionUserAsync(jwtPrincipal, tenantId, objectId, subjectHash, settings.Users, cancellationToken);
            if (!provisioning.Accepted)
            {
                return null;
            }

            beaconUserId = provisioning.BeaconUserId;
        }

        var displayName = GetClaim(jwtPrincipal, "name") ?? GetClaim(jwtPrincipal, "preferred_username") ?? "user";

        return new McpCaller(
            McpCallerKind.User,
            displayName,
            subjectHash,
            beaconUserId,
            ResolveUserProjects(settings.Users, roles, groups),
            settings.Users.Scope,
            HostClaims: []);
    }

    /// <summary>
    /// Entra app-only (client credentials) tokens carry <c>idtyp=app</c> when that optional claim is on, and never
    /// carry <c>scp</c>; delegated tokens always carry <c>scp</c>.
    /// </summary>
    internal static bool IsAppOnlyToken(ClaimsPrincipal principal)
    {
        if (string.Equals(GetClaim(principal, "idtyp"), "app", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !principal.HasClaim(x => x.Type == "scp");
    }

    private static McpSystemCallerOptions? FindSystem(
        IEnumerable<McpSystemCallerOptions> systems,
        string? objectId,
        string? clientId,
        bool isAppOnly)
    {
        var materialized = systems.ToList();

        if (!string.IsNullOrEmpty(objectId))
        {
            var byObjectId = materialized
                .Where(x => !string.IsNullOrWhiteSpace(x.ObjectId))
                .Where(x => string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (byObjectId != null)
            {
                return byObjectId;
            }
        }

        if (!isAppOnly || string.IsNullOrEmpty(clientId))
        {
            return null;
        }

        return materialized
            .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
            .Where(x => string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static bool IsUserAdmitted(McpUserCallerOptions users, HashSet<string> roles, HashSet<string> groups)
    {
        if (users.RequiredRoles.Count == 0 && users.RequiredGroups.Count == 0)
        {
            return true;
        }

        return users.RequiredRoles.Any(x => roles.Contains(x))
            || users.RequiredGroups.Any(x => groups.Contains(x));
    }

    private static List<int> ResolveUserProjects(McpUserCallerOptions users, HashSet<string> roles, HashSet<string> groups)
    {
        var grantedByMembership = users.GroupProjects
            .Where(x => roles.Contains(x.Key) || groups.Contains(x.Key))
            .SelectMany(x => x.Value);

        return users.ProjectIds
            .Concat(grantedByMembership)
            .Distinct()
            .Order()
            .ToList();
    }

    private async Task<(bool Accepted, int? BeaconUserId)> ProvisionUserAsync(
        ClaimsPrincipal principal,
        string tenantId,
        string objectId,
        string subjectHash,
        McpUserCallerOptions users,
        CancellationToken cancellationToken)
    {
        if (userManagementService == null)
        {
            logger.LogWarning(
                "MCP caller {CallerHash} accepted without a Beacon user: AutoProvision is on but user management is not enabled.",
                subjectHash);
            return (true, null);
        }

        var cacheKey = $"mcp-caller-user:{subjectHash}";
        if (cache.TryGetValue(cacheKey, out int cachedUserId))
        {
            return (true, cachedUserId);
        }

        var email = GetClaim(principal, "email") ?? GetClaim(principal, "upn");
        var displayName = GetClaim(principal, "name");
        var userName = GetClaim(principal, "preferred_username") ?? email ?? displayName ?? objectId;

        try
        {
            // Keyed on the Entra oid (stable across apps, unlike the pairwise sub) within the tenant. The provider
            // string is tenant-based rather than the issuer so v1 and v2 tokens resolve to the same user.
            var user = await userManagementService.GetOrCreateExternalUserAsync(
                objectId,
                $"entra:{tenantId}",
                userName,
                email,
                displayName,
                users.DefaultRoleName,
                cancellationToken);

            cache.Set(cacheKey, user.Id, ProvisionedUserCacheDuration);

            return (true, user.Id);
        }
        catch (BeaconException ex)
        {
            logger.LogWarning("MCP caller {CallerHash} rejected by user provisioning: {Reason}", subjectHash, ex.Message);
            return (false, null);
        }
    }

    private static string? GetClaim(ClaimsPrincipal principal, string type)
    {
        var value = principal.FindFirst(type)?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static HashSet<string> GetClaimValues(ClaimsPrincipal principal, string type)
    {
        return principal
            .FindAll(type)
            .Select(x => x.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
