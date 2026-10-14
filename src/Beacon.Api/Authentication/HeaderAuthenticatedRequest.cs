using Beacon.Core.Mcp;
using Microsoft.Net.Http.Headers;

namespace Beacon.Api.Authentication;

/// <summary>
/// Recognises a request whose caller <see cref="ApiKeyAuthMiddleware"/> or <see cref="JwtBearerAuthMiddleware"/>
/// authenticated from its <c>Authorization: Bearer</c> header. A browser never attaches that header to a cross-site
/// request on its own, so such a request cannot be forged: antiforgery validation is skipped for it and no antiforgery
/// token is issued to it. All three must hold — the header, the identity type those middlewares build and their
/// <c>auth_method</c> marker — so a cookie session never qualifies, whatever claims it carries.
/// </summary>
internal static class HeaderAuthenticatedRequest
{
    public static bool Is(HttpContext context)
    {
        var identity = context.User.Identity;
        if (identity?.IsAuthenticated != true)
        {
            return false;
        }

        var authorization = context.Request.Headers[HeaderNames.Authorization].ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var user = context.User;
        if (identity.AuthenticationType == McpCallerClaimTypes.ApiKeyAuthenticationType)
        {
            return user.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod);
        }

        return identity.AuthenticationType == JwtBearerAuthMiddleware.AuthenticationType
            && (user.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.JwtAuthMethod)
                || user.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod));
    }
}
