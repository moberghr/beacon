using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Services;

namespace Beacon.SampleProject.Services;

/// <summary>
/// Transforms claims after authentication to add Beacon role claims.
/// This example assigns roles based on username for demonstration purposes.
/// In production, you would query your database or external service.
/// </summary>
public class SampleClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        var username = identity.Name;

        // Add Beacon user claims
        if (!identity.HasClaim(c => c.Type == BeaconClaims.UserId))
        {
            identity.AddClaim(new Claim(BeaconClaims.UserId, username ?? "unknown"));
        }

        if (!identity.HasClaim(c => c.Type == BeaconClaims.UserName))
        {
            identity.AddClaim(new Claim(BeaconClaims.UserName, username ?? "unknown"));
        }

        // Assign roles based on username (for demo purposes)
        // In production, you would query your database or external service
        if (!identity.HasClaim(c => c.Type == BeaconClaims.Role))
        {
            var role = username?.ToLowerInvariant() switch
            {
                "admin" => RoleService.RoleNames.Admin,
                "editor" => RoleService.RoleNames.Editor,
                "viewer" => RoleService.RoleNames.Viewer,
                _ => RoleService.RoleNames.Viewer,
            };

            identity.AddClaim(new Claim(BeaconClaims.Role, role));
        }

        return Task.FromResult(principal);
    }
}
