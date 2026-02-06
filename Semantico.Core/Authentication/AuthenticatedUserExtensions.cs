using System.Security.Claims;

namespace Semantico.Core.Authentication;

public static class AuthenticatedUserExtensions
{
    public static List<Claim> ToClaims(this AuthenticatedUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId),
            new(ClaimTypes.Name, user.UserName)
        };

        if (!string.IsNullOrEmpty(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        if (!string.IsNullOrEmpty(user.DisplayName))
        {
            claims.Add(new Claim("DisplayName", user.DisplayName));
        }

        foreach (var role in user.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (var claim in user.Claims)
        {
            claims.Add(new Claim(claim.Key, claim.Value));
        }

        return claims;
    }
}
