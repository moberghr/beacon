using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Beacon.Api.Authentication;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Handlers.Users;
using Beacon.Core.Models;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Api.Endpoints;

/// <summary>
/// React SPA login endpoints — issue and clear the <c>Beacon.Auth</c> cookie.
/// SSO/OIDC challenge endpoints live in <c>SsoEndpoints.cs</c>; both are
/// wired by the single <see cref="MapLoginEndpoints"/> entry point.
/// </summary>
public static partial class LoginEndpoints
{
    private const string InvalidCredentials = "Invalid username or password.";
    private const string LoggerName = "Beacon.Api.Endpoints.LoginEndpoints";

    public static IEndpointRouteBuilder MapLoginEndpoints(
        this IEndpointRouteBuilder endpoints,
        string basePath,
        BeaconConfiguration configuration)
    {
        MapCookieAuth(endpoints, basePath, configuration);
        MapSsoChallenge(endpoints, basePath, configuration);
        return endpoints;
    }

    private static void MapCookieAuth(
        IEndpointRouteBuilder endpoints,
        string basePath,
        BeaconConfiguration configuration)
    {
        // POST /beacon/api/auth/login: password sign-in. Not mapped at all while local login is off and no break-glass
        // account is configured (Beacon:UserManagement:LocalLogin).
        var localLogin = configuration.UserManagement.LocalLogin;
        if (localLogin.IsOffered)
        {
            MapPasswordLogin(endpoints, basePath, configuration);
        }

        // POST /beacon/api/auth/logout[?everywhere=true]
        // Ends this session. With everywhere=true it first signs out every other session of the signed-in user too (a new
        // security stamp, SignUserOutEverywhereCommand). When that cannot be done (Beacon user management is off, or the
        // caller is no signed-in session of a stored user) nothing is signed out and the answer is 409, so the caller knows
        // their other sessions are still open.
        endpoints.MapPost($"{basePath}/api/auth/logout", async (
            HttpContext context,
            IBeaconAuthenticationProvider authProvider,
            IAntiforgery antiforgery,
            bool? everywhere) =>
        {
            if (everywhere == true)
            {
                var refusal = await SignOutEverywhereAsync(context);
                if (refusal != null)
                {
                    return refusal;
                }
            }

            await SignInAudit.SignedOutAsync(context);
            await authProvider.SignOutAsync();
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            // Reissue the antiforgery cookie for the now-anonymous user so a
            // subsequent login doesn't carry over a stale identity-bound token.
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            antiforgery.SetCookieTokenAndHeader(context);

            return Results.Ok(new { success = true });
        })
        .AllowAnonymous()
        // POST only (there is no GET sign-out endpoint), and only with a valid antiforgery token, anonymous callers
        // included: a cross-site form POST arrives without the SameSite=Lax session cookie, yet the sign-out answer
        // would still delete it. Without a token the request is refused before anything is signed out or any cookie is
        // emitted. A stale token (identity rotated in another tab) gets the same 400, and the SPA re-primes its token
        // and retries once (beaconFetch). The built-in antiforgery middleware stays off: the filter validates, and it
        // also accepts the SPA's X-XSRF-TOKEN header on a host that kept the default header name.
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithMetadata(AntiforgeryForAnonymousCallers.Instance)
        .DisableAntiforgery();
    }

    private static void MapPasswordLogin(
        IEndpointRouteBuilder endpoints,
        string basePath,
        BeaconConfiguration configuration)
    {
        var localLogin = configuration.UserManagement.LocalLogin;

        endpoints.MapPost($"{basePath}/api/auth/login", async (
            HttpContext context,
            IBeaconAuthenticationProvider authProvider,
            IAntiforgery antiforgery,
            LoginRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new LoginResponse { Success = false, Error = "Username and password are required." });
            }

            // Every name gets the same password check, local login on or off: whether an account may sign in is decided
            // after it (by the provider and the gate below), so neither the answer nor its timing tells a break-glass
            // account from any other.
            var result = await authProvider.AuthenticateAsync(request.Username, request.Password);

            // An administrator set the password: the user signs in by choosing a new one in the same request.
            if (result.PasswordChangeRequired && !string.IsNullOrEmpty(request.NewPassword))
            {
                var users = context.RequestServices.GetService<IUserManagementService>().OrNull();
                result = users == null
                    ? AuthenticationResult.Failed(InvalidCredentials)
                    : await users.CompleteRequiredPasswordChangeAsync(request.Username, request.Password, request.NewPassword, context.RequestAborted);
            }

            if (result.PasswordChangeRequired)
            {
                await SignInAudit.PasswordChangeRequiredAsync(context, result);
                var changePayload = new LoginResponse { Success = false, PasswordChangeRequired = true, Error = result.ErrorMessage };
                var status = string.IsNullOrEmpty(request.NewPassword)
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status400BadRequest;

                return Results.Json(changePayload, statusCode: status);
            }

            if (!result.Success || result.User == null)
            {
                await SignInAudit.PasswordFailedAsync(context, result, request.Username);
                var failurePayload = new LoginResponse { Success = false, Error = result.ErrorMessage ?? "Invalid credentials." };
                return Results.Json(failurePayload, statusCode: StatusCodes.Status401Unauthorized);
            }

            // Local login off: whichever provider authenticated the password, only a stored internal break-glass user signs
            // in. The name typed is never logged.
            if (!localLogin.Enabled)
            {
                var breakGlassUserId = await PasswordSignInGate.BreakGlassUserIdAsync(context.RequestServices, result, context.RequestAborted);
                if (breakGlassUserId == null)
                {
                    Logger(context)?.LogInformation(
                        ProjectAccessEvents.PasswordSignInRefused,
                        "Password sign-in refused: local login is off and the account is not a break-glass account");
                    await SignInAudit.PasswordRefusedAsync(context, result);

                    return Results.Json(new LoginResponse { Success = false, Error = InvalidCredentials }, statusCode: StatusCodes.Status401Unauthorized);
                }

                Logger(context)?.LogWarning(
                    ProjectAccessEvents.BreakGlassSignIn,
                    "Break-glass password sign-in while local login is off (user {UserId})",
                    breakGlassUserId);
            }

            var claims = result.User.ToClaims();
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            var authProperties = new AuthenticationProperties
            {
                IsPersistent = request.RememberMe,
                ExpiresUtc = request.RememberMe
                    ? DateTimeOffset.UtcNow.AddDays(configuration.Authentication.RememberMeExpirationDays)
                    : DateTimeOffset.UtcNow.AddHours(configuration.Authentication.CookieExpirationHours),
                AllowRefresh = true
            };

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProperties);

            // The user was disabled or archived, or their security stamp changed, between the password check and the
            // sign-in: the cookie carries no session (the session events recorded why).
            if (BeaconSessionEvents.WasSignInRefused(context))
            {
                return Results.Json(new LoginResponse { Success = false, Error = InvalidCredentials }, statusCode: StatusCodes.Status401Unauthorized);
            }

            await SignInAudit.SignedInAsync(context, result, breakGlass: !localLogin.Enabled);

            // Antiforgery tokens are bound to claims; the cookie issued
            // pre-login was minted for the anonymous user and won't validate
            // against the now-authenticated identity. Reissue here so the
            // first authenticated POST doesn't 400 with a "different claims"
            // mismatch.
            context.User = principal;
            antiforgery.SetCookieTokenAndHeader(context);

            // LoginRedirectPath is a full browser path, /beacon mount point
            // included — the SPA navigates to it with a full page load.
            return Results.Ok(new LoginResponse
            {
                Success = true,
                RedirectUrl = configuration.Authentication.LoginRedirectPath
            });
        })
        .AllowAnonymous()
        // Every answer is a LoginResponse: 200 signed in, 401 refused or a new password needed (passwordChangeRequired), 400
        // a missing field or a new password the rules refuse. The operation is documented only where it is mapped.
        .Produces<LoginResponse>(StatusCodes.Status200OK)
        .Produces<LoginResponse>(StatusCodes.Status400BadRequest)
        .Produces<LoginResponse>(StatusCodes.Status401Unauthorized)
        // Only with a valid antiforgery token, anonymous callers included (the SPA primes one from GET /csrf and sends it
        // like on every mutation): a cross-site form POST cannot sign the browser in to another account. Refused (400)
        // before the throttle or the password check; a caller authenticated by its Authorization header is not checked.
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithMetadata(AntiforgeryForAnonymousCallers.Instance)
        .AddEndpointFilter<LoginRateLimitFilter>()
        .DisableAntiforgery();
    }

    // Signs out every session of the caller (SignUserOutEverywhereCommand); the 409 answer when that could not be done.
    private static async Task<IResult?> SignOutEverywhereAsync(HttpContext context)
    {
        var mediator = context.RequestServices.GetService<IMediator>();
        if (mediator == null)
        {
            return Results.Problem(
                detail: "Signing out everywhere is not available on this host.",
                statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            await mediator.Send(new SignUserOutEverywhereCommand(null), context.RequestAborted);

            return null;
        }
        catch (BeaconConflictException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static ILogger? Logger(HttpContext context)
    {
        return context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(LoggerName);
    }
}

/// <summary>
/// A password sign-in. <see cref="NewPassword"/> is sent by a user who must choose a new password (an administrator set the
/// current one; the answer to the first attempt says so): it becomes their password and they are signed in.
/// </summary>
public record LoginRequest(string? Username, string? Password, bool RememberMe = false, string? NewPassword = null);

public record LoginResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RedirectUrl { get; init; }

    /// <summary>The credentials were right, but the user must choose a new password: send it as <c>newPassword</c>.</summary>
    public bool PasswordChangeRequired { get; init; }
}
