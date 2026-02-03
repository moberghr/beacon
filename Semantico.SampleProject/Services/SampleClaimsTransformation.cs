using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Semantico.Core.Authorization;

namespace Semantico.SampleProject.Services;

/// <summary>
/// Transforms claims after authentication to add Semantico role claims.
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

        // Add Semantico user claims
        if (!identity.HasClaim(c => c.Type == SemanticoClaims.UserId))
        {
            identity.AddClaim(new Claim(SemanticoClaims.UserId, username ?? "unknown"));
        }

        if (!identity.HasClaim(c => c.Type == SemanticoClaims.UserName))
        {
            identity.AddClaim(new Claim(SemanticoClaims.UserName, username ?? "unknown"));
        }

        // Assign roles based on username (for demo purposes)
        // In production, you would query your database or external service
        if (!identity.HasClaim(c => c.Type == SemanticoClaims.Role))
        {
            var role = username?.ToLowerInvariant() switch
            {
                "admin" => "Admin",      // Full access
                "editor" => "Editor",    // Read/Write, no delete
                "viewer" => "Viewer",    // Read-only
                _ => "Viewer"            // Default: Read-only
            };

            identity.AddClaim(new Claim(SemanticoClaims.Role, role));
        }

        return Task.FromResult(principal);
    }
}
