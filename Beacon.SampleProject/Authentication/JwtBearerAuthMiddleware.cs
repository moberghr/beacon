using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;

namespace Beacon.SampleProject.Authentication;

/// <summary>
/// Middleware that validates JWT bearer tokens from the Authorization header.
/// Sets HttpContext.User for stateless authentication (no cookie created).
/// </summary>
internal sealed class JwtBearerAuthMiddleware(
    RequestDelegate next,
    JwtAuthenticationOptions options,
    ILogger<JwtBearerAuthMiddleware> logger)
{
    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext context, JwtExternalApiAuthenticationProvider jwtProvider)
    {
        // Skip if user already authenticated (e.g., via cookie)
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        // Skip if bearer auth not enabled
        if (!options.EnableBearerAuthentication)
        {
            await next(context);
            return;
        }

        // Check for Authorization header
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var token = authHeader[BearerPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            await next(context);
            return;
        }

        // Validate the token
        var result = await jwtProvider.ValidateTokenAsync(token);
        if (!result.Success || result.User == null)
        {
            logger.LogDebug("JWT bearer token validation failed: {Error}", result.ErrorMessage);

            // For API requests, return 401
            if (IsApiRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "unauthorized",
                    message = result.ErrorMessage ?? "Invalid token"
                });
                return;
            }

            // For other requests, continue without authentication
            await next(context);
            return;
        }

        // Build claims principal
        var claims = result.User.ToClaims();
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Set the user on the context (stateless - no cookie)
        context.User = principal;

        logger.LogDebug("JWT bearer authentication successful for user {UserId}", result.User.UserId);

        await next(context);
    }

    private static bool IsApiRequest(HttpContext context)
    {
        // Check if this is an API request (JSON expected)
        var acceptHeader = context.Request.Headers.Accept.FirstOrDefault();
        if (acceptHeader?.Contains("application/json") == true)
        {
            return true;
        }

        // Check if path contains /api/
        if (context.Request.Path.Value?.Contains("/api/") == true)
        {
            return true;
        }

        return false;
    }
}
