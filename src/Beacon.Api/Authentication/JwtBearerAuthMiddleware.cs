using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Mcp;

namespace Beacon.Api.Authentication;

/// <summary>
/// Middleware that validates JWT bearer tokens from the Authorization header.
/// Sets HttpContext.User for stateless authentication (no cookie created).
/// <para>
/// Token-supplied claims of the <see cref="McpCallerClaimTypes.Reserved"/> types (<c>allowed_projects</c>,
/// <c>scope</c>, <c>auth_method</c>, <c>api_key_id</c>, <c>caller_*</c>) are stripped on every route: those claims are
/// minted by Beacon only, and a token must never grant itself projects, a scope or an audit identity.
/// </para>
/// <para>
/// On <c>/beacon/mcp</c> the validated token is passed to <see cref="IMcpCallerMapper"/>, which decides the caller's
/// projects, scope and Beacon user. Every JWT principal there carries <c>auth_method=mcp_caller</c>, so the
/// Execute-scope policy gates it like an API key; an unmapped caller has no scope and is rejected with 403. Other
/// routes keep the pre-existing behaviour (claims pass through, <c>auth_method=jwt</c>, not scope-gated).
/// </para>
/// </summary>
internal sealed class JwtBearerAuthMiddleware(
    RequestDelegate next,
    JwtAuthenticationOptions options,
    ILogger<JwtBearerAuthMiddleware> logger)
{
    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(
        HttpContext context,
        JwtExternalApiAuthenticationProvider jwtProvider,
        IMcpCallerMapper callerMapper)
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

        var claims = result.User
            .ToClaims()
            .Where(x => !McpCallerClaimTypes.Reserved.Contains(x.Type))
            .ToList();

        if (!context.Request.Path.StartsWithSegments(McpDiscoveryPaths.McpPath))
        {
            claims.Add(new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.JwtAuthMethod));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

            logger.LogDebug("JWT bearer authentication successful for user {UserId}", result.User.UserId);

            await next(context);
            return;
        }

        claims.Add(new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod));

        var tokenPrincipal = result.TokenPrincipal ?? new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        var caller = await callerMapper.MapAsync(tokenPrincipal, context.RequestAborted);
        if (caller != null)
        {
            AddCallerClaims(claims, caller);
            context.Items[typeof(McpCaller)] = caller;

            logger.LogDebug(
                "MCP JWT caller {CallerHash} mapped as {CallerKind} with {ProjectCount} project(s)",
                caller.SubjectHash,
                caller.Kind,
                caller.AllowedProjectIds.Count);
        }

        // An unmapped caller stays authenticated but carries no scope and no projects: the Execute-scope policy
        // answers 403 and ProjectContextFactory fails closed.
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        await next(context);
    }

    private static void AddCallerClaims(List<Claim> claims, McpCaller caller)
    {
        // NameIdentifier is the Beacon user id when one was provisioned, which is what ProjectContextFactory parses
        // for audit. Otherwise it is the subject hash, which never parses as an int, so a numeric token sub can never
        // be mistaken for somebody else's Beacon user id.
        claims.RemoveAll(x => x.Type == ClaimTypes.NameIdentifier);
        claims.Add(new Claim(ClaimTypes.NameIdentifier, caller.BeaconUserId?.ToString() ?? caller.SubjectHash));
        claims.Add(new Claim(McpCallerClaimTypes.AllowedProjects, JsonSerializer.Serialize(caller.AllowedProjectIds)));
        claims.Add(new Claim(McpCallerClaimTypes.Scope, caller.Scope.ToString()));
        claims.Add(new Claim(McpCallerClaimTypes.CallerKind, caller.Kind.ToString()));
        claims.Add(new Claim(McpCallerClaimTypes.CallerHash, caller.SubjectHash));
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
