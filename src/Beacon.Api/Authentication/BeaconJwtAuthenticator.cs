using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
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
/// Judges the JWT bearer token in a request's <c>Authorization</c> header, for <see cref="BeaconJwtAuthenticationHandler"/>
/// and the bearer middleware. One instance per configuration: it keeps the refusal-log throttle.
/// <para>
/// A token is proof of identity only. Beacon never takes roles, projects, a scope, a Beacon user id or an audit
/// identity from it: on <c>/beacon/mcp</c> the principal is built from an allow-list (name, e-mail, display name) plus
/// what the mapper decides; on every other Beacon route it is the bound Beacon user, with no token claim at all.
/// </para>
/// <para>
/// On <c>/beacon/mcp</c> the validated token is passed to <see cref="IMcpCallerMapper"/>, which decides the caller's
/// projects, scope and Beacon user. Every JWT principal there carries <c>auth_method=mcp_caller</c>, so the
/// Execute-scope policy gates it like an API key; an unmapped caller has no scope and is rejected with 403. The principal
/// is also kept on the request as <see cref="McpCallerAuthenticationFeature"/>.
/// </para>
/// <para>
/// On every other Beacon route the token must be an access token (positive evidence, never inferred from <c>roles</c>), must
/// pass the SSO admission rules when the SSO authority issued it, and must name an existing, enabled, non-archived
/// external Beacon user — the same screen and binding as the login-form JWT flow
/// (<see cref="BearerUserBinding.ScreenAndBindAsync"/>); the principal is that user, with that user's Beacon roles,
/// and carries <c>auth_method=jwt</c>. Any other token is a failure: an API request is answered with a generic 401
/// (<c>WWW-Authenticate: Bearer error="invalid_token"</c>), another continues anonymously. Refusals are logged with a
/// reason code, the tenant, the issuer and a subject hash — at Warning at most once a minute, otherwise at Debug.
/// </para>
/// </summary>
internal sealed class BeaconJwtAuthenticator(JwtAuthenticationOptions options, ILogger logger)
{
    /// <summary>Authentication type of every identity this authenticator builds.</summary>
    internal const string AuthenticationType = "Bearer";

    private const string BearerPrefix = "Bearer ";
    private const string RejectedTokenDetail = "The bearer token was not accepted.";
    // RFC 6750 §3.1. On /beacon/mcp, UseMcpResourceMetadataChallenge appends resource_metadata to it.
    private const string InvalidTokenChallenge = "Bearer error=\"invalid_token\"";
    private const string SigningKeysRetryAfterSeconds = "30";
    private static readonly long RefusalWarningIntervalMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    private int _warnedNoUserStore;
    private long _nextRefusalWarningAt;

    public bool Enabled => options.EnableBearerAuthentication;

    /// <summary>Whether <paramref name="authorization"/> carries a non-empty bearer value.</summary>
    public static bool HasBearerToken(string? authorization)
    {
        return !string.IsNullOrEmpty(authorization)
            && authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(authorization[BearerPrefix.Length..].Trim());
    }

    public async Task<AuthenticateResult> AuthenticateAsync(
        HttpContext context,
        JwtExternalApiAuthenticationProvider jwtProvider,
        IMcpCallerMapper callerMapper)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (!options.EnableBearerAuthentication || !HasBearerToken(authHeader))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authHeader![BearerPrefix.Length..].Trim();

        // The provider never throws for a bad token (malformed, wrong algorithm, unknown key...): every such value comes
        // back as a failed result, so a hostile bearer value is a 401, never a 500.
        var result = await jwtProvider.ValidateTokenAsync(token, context.RequestAborted);
        if (result.SigningKeysUnavailable && BeaconApiRequest.Is(context))
        {
            // The token was never judged: a 401 would make an OAuth client discard a possibly valid token and re-run its
            // authorization flow, which cannot fix an unreachable JWKS endpoint.
            return AuthenticateResult.Fail(BeaconCredentialRefusal.Unavailable(
                result.ErrorMessage ?? "The token signing keys are unavailable.",
                "temporarily_unavailable",
                SigningKeysRetryAfterSeconds));
        }

        if (!result.Success || result.User == null || result.TokenPrincipal == null)
        {
            // The caller only learns that the token was not accepted; the fixed reason string goes to the log.
            LogRefusal(BearerRefusal.InvalidToken, tokenPrincipal: null, result.ErrorMessage);
            return Reject(context);
        }

        if (!context.Request.Path.StartsWithSegments(McpDiscoveryPaths.McpPath))
        {
            var binding = await BindBeaconUserAsync(context, result.TokenPrincipal, result.TokenType);
            if (binding.User == null)
            {
                LogRefusal(binding.Refusal, result.TokenPrincipal, detail: null);
                return Reject(context);
            }

            var userClaims = BearerUserBinding.ToAuthenticatedUser(binding.User).ToClaims();
            userClaims.Add(new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.JwtAuthMethod));

            logger.LogDebug("JWT bearer authentication successful for Beacon user {UserId}", binding.User.Id);

            return Success(new ClaimsPrincipal(new ClaimsIdentity(userClaims, AuthenticationType)));
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
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
        context.Features.Set(new McpCallerAuthenticationFeature(principal));

        return Success(principal);
    }

    private static AuthenticateResult Success(ClaimsPrincipal principal)
    {
        return AuthenticateResult.Success(new AuthenticationTicket(principal, AuthenticationType));
    }

    // An API request is answered with the generic 401; any other request continues anonymously (the login redirect applies).
    private static AuthenticateResult Reject(HttpContext context)
    {
        return AuthenticateResult.Fail(BeaconCredentialRefusal.Unauthorized(
            RejectedTokenDetail,
            answersRequest: BeaconApiRequest.Is(context),
            error: "invalid_token",
            challenge: InvalidTokenChallenge));
    }

    private async Task<BearerBinding> BindBeaconUserAsync(
        HttpContext context,
        ClaimsPrincipal tokenPrincipal,
        string? tokenType)
    {
        var users = context.RequestServices.GetService<IUserManagementService>().OrNull();
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
}
