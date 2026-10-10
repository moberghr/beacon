using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Beacon.Core.Mcp;
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
    /// The signed-in user. Throws <see cref="UnauthorizedAccessException"/> for a caller that is not an interactive
    /// session, and <see cref="InvalidOperationException"/> when no user matches (an archived user is not found).
    /// </summary>
    public static async Task<BeaconUserData> ResolveAsync(
        IHttpContextAccessor httpContextAccessor,
        IUserManagementService userManagementService,
        CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (!IsInteractiveSession(principal))
        {
            throw new UnauthorizedAccessException(InteractiveSessionRequired);
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
    /// The signed-in user (see <see cref="ResolveAsync"/>), when they are an administrator now: enabled, and a super
    /// admin or holding the Admin role in the user store. The Admin-role route policy reads the role claims the session
    /// was signed in with, which a later role change does not update. Throws <see cref="UnauthorizedAccessException"/>
    /// for a user who is not an administrator now.
    /// </summary>
    public static async Task<BeaconUserData> ResolveAdministratorAsync(
        IHttpContextAccessor httpContextAccessor,
        IUserManagementService userManagementService,
        CancellationToken cancellationToken)
    {
        var user = await ResolveAsync(httpContextAccessor, userManagementService, cancellationToken);
        var isAdministrator = user.IsSuperAdmin
            || user.Roles.Any(x => x.Name == RoleService.RoleNames.Admin);
        if (!user.IsEnabled || !isAdministrator)
        {
            throw new UnauthorizedAccessException(AdministratorRequired);
        }

        return user;
    }
}
