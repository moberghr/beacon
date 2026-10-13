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
    /// Work with no request in a scope marked as system work (a background job, a startup task): owns nothing and is no
    /// Admin, like <see cref="Nobody"/>, but the project access evaluator reads it as the system.
    /// </summary>
    internal static BeaconActor OffRequest { get; } = new(null, false) { Kind = BeaconPrincipalKind.System };

    /// <summary>
    /// The other ids the caller's request presents for itself: the id <c>/auth/me</c> reports and the
    /// <c>NameIdentifier</c> (an API key's numeric owner id). A request may name the caller by them. Empty without a
    /// <see cref="UserId"/>.
    /// </summary>
    internal IReadOnlyList<string> SessionIds { get; init; } = [];

    /// <summary>The caller is an API key: its session ids never match a stored assignee.</summary>
    internal bool IsApiKey { get; init; }

    /// <summary>How the project access evaluator classifies the caller.</summary>
    internal BeaconPrincipalKind Kind { get; init; } = BeaconPrincipalKind.Anonymous;

    /// <summary>The <c>Users.Id</c> of the caller's stored, active user (an API key's owner); null without one.</summary>
    public int? BeaconUserId { get; internal init; }

    /// <summary>True when <see cref="BeaconUserId"/> names a stored user that is enabled and not archived.</summary>
    internal bool IsActiveUser { get; init; }

    /// <summary>
    /// The caller's live Admin standing: a super admin or a holder of the Admin role in the user store now, whatever
    /// role claims the session was signed in with. False without a stored user.
    /// </summary>
    internal bool IsLiveAdmin { get; init; }

    /// <summary>The highest level among the stored user's roles now (a super admin counts as Admin); 0 without a user.</summary>
    internal int RoleLevel { get; init; }

    /// <summary>
    /// The projects an <c>allowed_projects</c> claim restricts the caller to (an API key with a project list, a mapped MCP
    /// caller); null without the claim. A blank or malformed claim is empty.
    /// </summary>
    internal IReadOnlyList<int>? AllowedProjectIds { get; init; }

    /// <summary>A scoped caller's scope (an API key, a mapped MCP caller); null for any other caller or none presented.</summary>
    internal McpCallerScope? ScopeCeiling { get; init; }

    /// <summary>A mapped MCP caller's <c>caller_kind</c>; null for any other caller.</summary>
    internal McpCallerKind? CallerKind { get; init; }

    /// <summary>The API key's id, for an API key; null for any other caller.</summary>
    internal int? ApiKeyId { get; init; }

    /// <summary>
    /// The one id access and security logs name the caller by: its <c>Users.Id</c>, or, without a stored user (a host
    /// principal, an Admin of a host without Beacon user management), the id its session presents
    /// (<see cref="UserId"/>); null for a caller without either.
    /// </summary>
    public string? LogId => BeaconUserId?.ToString(CultureInfo.InvariantCulture) ?? UserId;

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
    /// True when <paramref name="recordedUserId"/>, an id Beacon recorded for a person (a change's requester or reviewer),
    /// names this caller by any id its request presents: its <see cref="UserId"/>, a session id (what earlier versions
    /// recorded for a session), or its <c>Users.Id</c> (what earlier versions recorded for an API key's owner), ignoring
    /// case (an identity provider may change the case of a subject it issues). Broader than <see cref="Is"/>, for refusing
    /// someone their own approval: a false match only refuses.
    /// </summary>
    public bool IsRecordedAs(string? recordedUserId)
    {
        if (string.IsNullOrEmpty(recordedUserId))
        {
            return false;
        }

        return (UserId != null && string.Equals(UserId, recordedUserId, StringComparison.OrdinalIgnoreCase))
            || SessionIds.Contains(recordedUserId, StringComparer.OrdinalIgnoreCase)
            || (BeaconUserId is { } id && recordedUserId == id.ToString(CultureInfo.InvariantCulture));
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
/// resolves); a key without them names nobody. A mapped MCP caller names it by the numeric <c>Users.Id</c> in its
/// <c>NameIdentifier</c> as well (<see cref="UserId"/>), when one was provisioned. Every other principal names it by
/// <c>NameIdentifier</c>
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

            return new BeaconUserLookup(PositiveId(nameIdentifier), string.IsNullOrEmpty(userName) ? null : userName, null);
        }

        // A mapped MCP caller's NameIdentifier is its provisioned Users.Id, or a subject hash that never parses as one.
        if (IsMcpCaller(principal))
        {
            return new BeaconUserLookup(PositiveId(nameIdentifier), null, null);
        }

        return new BeaconUserLookup(null, null, string.IsNullOrEmpty(nameIdentifier) ? null : nameIdentifier);
    }

    public static bool IsApiKey(ClaimsPrincipal principal)
    {
        return principal.Identity?.AuthenticationType == McpCallerClaimTypes.ApiKeyAuthenticationType
            && principal.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod);
    }

    public static bool IsMcpCaller(ClaimsPrincipal principal)
    {
        return principal.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod);
    }

    private static int? PositiveId(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
    }
}
