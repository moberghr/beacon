using System.Globalization;
using System.Security.Claims;
using Beacon.Core.Mcp;
using Beacon.Core.Services;

namespace Beacon.Core.Authorization;

/// <summary>
/// The signed-in caller as the actor that owns and works Beacon resources: alert tasks (assignee, resolver), data
/// contracts (owner) and AI actors (creator). Those resources store <see cref="UserId"/>, the caller's
/// <c>Users.ExternalId</c>. A session with no stored user (a host without Beacon user management) keeps the
/// <c>NameIdentifier</c> it presents; a disabled or archived user, an API key whose owner is not found, and an anonymous
/// request have no <see cref="UserId"/>. <see cref="IsAdmin"/> is the <c>ClaimTypes.Role</c> Admin claim the
/// <c>BeaconApiAdmin</c> policy requires, held by an enabled user (an API key carries no role). Resolved per request by
/// <see cref="IBeaconActorAccessor"/>.
/// </summary>
public sealed record BeaconActor(string? UserId, bool IsAdmin)
{
    /// <summary>An anonymous request, or a caller whose user cannot be resolved: owns nothing and is no Admin.</summary>
    public static BeaconActor Nobody { get; } = new(null, false);

    /// <summary>
    /// The other ids the caller's request presents for itself: the id <c>/auth/me</c> reports and the
    /// <c>NameIdentifier</c> (an API key's numeric owner id). A request may name the caller by them. Empty without a
    /// <see cref="UserId"/>.
    /// </summary>
    internal IReadOnlyList<string> SessionIds { get; init; } = [];

    /// <summary>The caller is an API key: its session ids never match a stored assignee.</summary>
    internal bool IsApiKey { get; init; }

    /// <summary>True when <paramref name="userId"/> is this caller.</summary>
    public bool Is(string? userId)
    {
        return UserId != null && userId == UserId;
    }

    /// <summary>True for an Admin, or when <paramref name="ownerUserId"/> is this caller.</summary>
    public bool IsOwnerOrAdmin(string? ownerUserId)
    {
        return IsAdmin || Is(ownerUserId);
    }

    /// <summary>
    /// True when a task's stored assignee names this caller: its <see cref="UserId"/>, or, for a signed-in session, one
    /// of its session ids, which earlier versions stored as the assignee of a task claimed through the UI.
    /// </summary>
    public bool IsAssignee(string? assigneeUserId)
    {
        if (Is(assigneeUserId))
        {
            return true;
        }

        return !IsApiKey && UserId != null && assigneeUserId != null && SessionIds.Contains(assigneeUserId);
    }

    /// <summary>The Admin role claim, as the <c>BeaconApiAdmin</c> policy requires it.</summary>
    public static bool HoldsAdminRole(ClaimsPrincipal? principal)
    {
        return principal?.HasClaim(ClaimTypes.Role, RoleService.RoleNames.Admin) == true;
    }
}

/// <summary>
/// How a principal names its Beacon user. An API key names its owner twice: by the numeric <c>Users.Id</c> in its
/// <c>NameIdentifier</c> (<see cref="UserId"/>, what <see cref="BeaconActor"/> and <see cref="IActorUserResolver"/>
/// resolve) and by the owner's user-name claim (<see cref="UserName"/>, what the database authorization provider
/// resolves); a key without them names nobody. Every other principal names it by <c>NameIdentifier</c>
/// (<see cref="ExternalId"/>), where cookie logins put <c>Users.ExternalId</c> and OIDC the subject. Not
/// <c>IBeaconUserContext.UserId</c>: it prefers <c>BeaconClaims.UserId</c>, which a host's claims transformation may set
/// to the user name.
/// </summary>
internal readonly record struct BeaconUserLookup(int? UserId, string? UserName, string? ExternalId)
{
    public static BeaconUserLookup Of(ClaimsPrincipal? principal)
    {
        if (principal == null)
        {
            return default;
        }

        var nameIdentifier = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (IsApiKey(principal))
        {
            var userName = principal.FindFirst(McpCallerClaimTypes.UserNameClaim)?.Value;
            var ownerId = int.TryParse(nameIdentifier, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : (int?)null;

            return new BeaconUserLookup(ownerId, string.IsNullOrEmpty(userName) ? null : userName, null);
        }

        return new BeaconUserLookup(null, null, string.IsNullOrEmpty(nameIdentifier) ? null : nameIdentifier);
    }

    public static bool IsApiKey(ClaimsPrincipal principal)
    {
        return principal.Identity?.AuthenticationType == McpCallerClaimTypes.ApiKeyAuthenticationType
            && principal.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod);
    }
}
