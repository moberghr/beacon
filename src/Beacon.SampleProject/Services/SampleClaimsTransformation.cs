using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Services;

namespace Beacon.SampleProject.Services;

/// <summary>
/// Transforms claims after authentication to add Beacon role claims.
/// The role is resolved from the Beacon user store by the session's user id (<see cref="ClaimTypes.NameIdentifier"/>,
/// the user's external id) — never by a display name and never inferred from the username string — so neither
/// self-registration nor a look-alike name can grant privileges. A user without any role gets no role claim.
/// </summary>
public class SampleClaimsTransformation(IUserManagementService userService) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return principal;
        }

        var username = identity.Name;

        if (!identity.HasClaim(x => x.Type == BeaconClaims.UserId))
        {
            identity.AddClaim(new Claim(BeaconClaims.UserId, username ?? "unknown"));
        }

        if (!identity.HasClaim(x => x.Type == BeaconClaims.UserName))
        {
            identity.AddClaim(new Claim(BeaconClaims.UserName, username ?? "unknown"));
        }

        var externalId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (identity.HasClaim(x => x.Type == BeaconClaims.Role) || string.IsNullOrWhiteSpace(externalId))
        {
            return principal;
        }

        var user = await userService.GetUserByExternalIdAsync(externalId);
        if (user == null || !user.IsEnabled)
        {
            return principal;
        }

        var role = ResolveRole(user);
        if (role != null)
        {
            identity.AddClaim(new Claim(BeaconClaims.Role, role));
        }

        return principal;
    }

    private static string? ResolveRole(Beacon.Core.Models.UserManagement.BeaconUserData user)
    {
        if (user.IsSuperAdmin)
        {
            return RoleService.RoleNames.Admin;
        }

        if (!user.Roles.Any())
        {
            return null;
        }

        var maxLevel = user.Roles.Max(x => x.Level);

        if (maxLevel >= RoleService.RoleLevels.Admin)
        {
            return RoleService.RoleNames.Admin;
        }

        if (maxLevel >= RoleService.RoleLevels.Editor)
        {
            return RoleService.RoleNames.Editor;
        }

        return RoleService.RoleNames.Viewer;
    }
}
