using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.ApiKeys;

/// <summary>
/// The caller of an API-key management request (listing, creating and revoking keys, the administrator routes too).
/// Keys are managed from a signed-in interactive session only: a principal carrying an <c>auth_method</c> claim — an
/// API key, an MCP caller or a REST bearer token — is refused, so no credential can list, create or revoke keys. The
/// user is resolved the way the authorization stack resolves it (<c>NameIdentifier</c> to <c>Users.ExternalId</c>), and
/// every decision about them (enabled, write permission, administrator) is taken from that one record.
/// </summary>
internal static class ApiKeyManagementCaller
{
    public const string InteractiveSessionRequired = "API keys can only be managed from a signed-in session.";

    public const string AdministratorRequired = "Only an administrator can manage every user's API keys.";

    public static bool IsInteractiveSession(ClaimsPrincipal? principal)
    {
        return principal?.Identity?.IsAuthenticated == true
            && !principal.HasClaim(x => x.Type == McpCallerClaimTypes.AuthMethod);
    }

    /// <summary>
    /// The signed-in user. Throws <see cref="BeaconForbiddenException"/> for a caller that is not an interactive
    /// session, and <see cref="InvalidOperationException"/> when no user matches (an archived user is not found).
    /// </summary>
    public static async Task<BeaconUserData> ResolveAsync(
        IBeaconRequestPrincipal requestPrincipal,
        IUserManagementService userManagementService,
        CancellationToken cancellationToken)
    {
        var principal = requestPrincipal.User;
        if (!IsInteractiveSession(principal))
        {
            throw new BeaconForbiddenException(InteractiveSessionRequired);
        }

        var externalId = principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(externalId))
        {
            throw new InvalidOperationException("Cannot manage API keys without an authenticated user.");
        }

        return await userManagementService.GetUserByExternalIdAsync(externalId, cancellationToken)
            ?? throw new InvalidOperationException("The signed-in user was not found.");
    }

    /// <summary>
    /// The signed-in user (see <see cref="ResolveAsync"/>), when they administer every user's keys now: enabled, and a
    /// super admin, holding the Admin role in the user store, or holding one of <paramref name="grantingPermissions"/>
    /// (a UserAdmin revokes keys; an Auditor or a UserAdmin lists them). The route policies read the role claims the
    /// session was signed in with, which a later role change does not update. Throws
    /// <see cref="BeaconForbiddenException"/> for a user who does not administer keys now.
    /// </summary>
    public static async Task<BeaconUserData> ResolveAdministratorAsync(
        IBeaconRequestPrincipal requestPrincipal,
        IUserManagementService userManagementService,
        IBeaconPermissionAccessor permissionAccessor,
        IReadOnlyCollection<string> grantingPermissions,
        CancellationToken cancellationToken)
    {
        var user = await ResolveAsync(requestPrincipal, userManagementService, cancellationToken);
        var isAdministrator = user.IsSuperAdmin
            || user.Roles.Any(x => x.Name == RoleService.RoleNames.Admin)
            || (await permissionAccessor.GetCurrentAsync(cancellationToken)).HoldsAny(grantingPermissions);
        if (!user.IsEnabled || !isAdministrator)
        {
            throw new BeaconForbiddenException(AdministratorRequired);
        }

        return user;
    }

    /// <summary>
    /// Whether the administrator <paramref name="user"/> (see <see cref="ResolveAdministratorAsync"/>) is an Admin now: a
    /// super admin, holding the Admin role in the user store, or an Admin by <paramref name="permissions"/>.
    /// </summary>
    public static bool IsAdmin(BeaconUserData user, BeaconPermissionSet permissions)
    {
        return user.IsSuperAdmin
            || user.Roles.Any(x => x.Name == RoleService.RoleNames.Admin)
            || permissions.IsAdmin;
    }
}
