using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;

namespace Beacon.Api.Authentication;

/// <summary>
/// Middleware that validates JWT bearer tokens from the Authorization header.
/// Sets HttpContext.User for stateless authentication (no cookie created).
/// <para>
/// A token is proof of identity only. Beacon never takes roles, projects, a scope, a Beacon user id or an audit
/// identity from it: on <c>/beacon/mcp</c> the principal is built from an allow-list (name, e-mail, display name) plus
/// what the mapper decides; on every other route it is the bound Beacon user, with no token claim at all.
/// </para>
/// <para>
/// On <c>/beacon/mcp</c> the validated token is passed to <see cref="IMcpCallerMapper"/>, which decides the caller's
/// projects, scope and Beacon user. Every JWT principal there carries <c>auth_method=mcp_caller</c>, so the
/// Execute-scope policy gates it like an API key; an unmapped caller has no scope and is rejected with 403.
/// </para>
/// <para>
/// On every other route the token must be an access token (positive evidence, never inferred from <c>roles</c>), must
/// pass the SSO admission rules when the SSO authority issued it, and must name an existing, enabled, non-archived
/// external Beacon user — the same screen and binding as the login-form JWT flow
/// (<see cref="BearerUserBinding.ScreenAndBindAsync"/>); the principal is that user, with that user's Beacon roles,
/// and carries <c>auth_method=jwt</c>. Any other token is refused: API requests get a generic 401
/// (<c>WWW-Authenticate: Bearer error="invalid_token"</c>), other requests continue anonymously. Refusals are logged
/// with a reason code, the tenant, the issuer and a subject hash — at Warning at most once a minute, otherwise at Debug.
/// </para>
/// </summary>
internal sealed class JwtBearerAuthMiddleware(
    RequestDelegate next,
    JwtAuthenticationOptions options,
    ILogger<JwtBearerAuthMiddleware> logger)
{
    private const string BearerPrefix = "Bearer ";
    private const string InvalidTokenChallenge = "Bearer error=\"invalid_token\"";
    private static readonly long RefusalWarningIntervalMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    private int _warnedNoUserStore;
    private long _nextRefusalWarningAt;

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

        var result = await jwtProvider.ValidateTokenAsync(token, context.RequestAborted);
        if (!result.Success || result.User == null || result.TokenPrincipal == null)
        {
            // The caller only learns that the token was not accepted; the fixed reason string goes to the log.
            LogRefusal(BearerRefusal.InvalidToken, tokenPrincipal: null, result.ErrorMessage);
            await RejectAsync(context);
            return;
        }

        if (!context.Request.Path.StartsWithSegments(McpDiscoveryPaths.McpPath))
        {
            var binding = await BindBeaconUserAsync(context, result.TokenPrincipal, result.TokenType);
            if (binding.User == null)
            {
                LogRefusal(binding.Refusal, result.TokenPrincipal, detail: null);
                await RejectAsync(context);
                return;
            }

            var userClaims = BearerUserBinding.ToAuthenticatedUser(binding.User).ToClaims();
            userClaims.Add(new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.JwtAuthMethod));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(userClaims, "Bearer"));

            logger.LogDebug("JWT bearer authentication successful for Beacon user {UserId}", binding.User.Id);

            await next(context);
            return;
        }

        // Allow-list: identity only. Token roles, groups, beacon:* claims, a token-supplied name identifier and the
        // reserved MCP claim types never reach the principal, whatever their spelling.
        var claims = new AuthenticatedUser
        {
            UserId = result.User.UserId,
            UserName = result.User.UserName,
            Email = result.User.Email,
            DisplayName = result.User.DisplayName
        }.ToClaims();
        claims.Add(new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod));

        var caller = await callerMapper.MapAsync(result.TokenPrincipal, context.RequestAborted);
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
        else
        {
            // An unmapped caller keeps no token-derived name identifier either: only the mapper sets one.
            claims.RemoveAll(x => x.Type == ClaimTypes.NameIdentifier);
        }

        // An unmapped caller stays authenticated but carries no scope and no projects: the Execute-scope policy
        // answers 403 and ProjectContextFactory fails closed.
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        await next(context);
    }

    private async Task<BearerBinding> BindBeaconUserAsync(
        HttpContext context,
        ClaimsPrincipal tokenPrincipal,
        string? tokenType)
    {
        var users = context.RequestServices.GetService<IUserManagementService>();
        if (users == null)
        {
            if (Interlocked.Exchange(ref _warnedNoUserStore, 1) == 0)
            {
                logger.LogWarning(
                    "Bearer tokens are refused outside /beacon/mcp: binding a token to a Beacon user needs user management.");
            }

            return BearerBinding.Refused(BearerRefusal.NoUserStore);
        }

        var oidc = context.RequestServices.GetService<IOptions<OidcAuthenticationOptions>>()?.Value;

        return await BearerUserBinding.ScreenAndBindAsync(
            users,
            tokenPrincipal,
            tokenType,
            options,
            oidc,
            logger,
            context.RequestAborted);
    }

    // Reason code, tenant, issuer and a subject hash: never the token, the raw subject, an e-mail or other claim values.
    private void LogRefusal(BearerRefusal reason, ClaimsPrincipal? tokenPrincipal, string? detail)
    {
        var level = TryClaimRefusalWarning() ? LogLevel.Warning : LogLevel.Debug;
        if (!logger.IsEnabled(level))
        {
            return;
        }

        var tenantId = tokenPrincipal == null ? null : BearerUserBinding.ExactClaim(tokenPrincipal, "tid");
        var issuer = tokenPrincipal == null ? null : BearerUserBinding.ExactClaim(tokenPrincipal, "iss");
        var subject = tokenPrincipal == null ? null : BearerUserBinding.ExactClaim(tokenPrincipal, "sub");

        logger.Log(
            level,
            "Bearer token refused: {Reason} {Detail} (tenant {TenantId}, issuer {Issuer}, subject {SubjectHash}). Further refusals within a minute are logged at Debug.",
            reason,
            detail ?? string.Empty,
            tenantId ?? "-",
            issuer ?? "-",
            SubjectFingerprint.Of(subject));
    }

    // At most one Warning per interval across all requests; the rest are logged at Debug.
    private bool TryClaimRefusalWarning()
    {
        var now = Environment.TickCount64;
        var next = Interlocked.Read(ref _nextRefusalWarningAt);

        return now >= next
            && Interlocked.CompareExchange(ref _nextRefusalWarningAt, now + RefusalWarningIntervalMs, next) == next;
    }

    private async Task RejectAsync(HttpContext context)
    {
        if (!IsApiRequest(context))
        {
            // Non-API requests continue unauthenticated (the login redirect applies).
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = InvalidTokenChallenge;
        await context.Response.WriteAsJsonAsync(
            new
            {
                error = "invalid_token",
                message = "The bearer token was not accepted."
            },
            context.RequestAborted);
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
