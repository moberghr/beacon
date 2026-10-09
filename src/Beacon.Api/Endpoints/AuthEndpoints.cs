using System.Security.Claims;
using Beacon.Api.Authentication;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Endpoints;

internal static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/auth/me", GetCurrentUser)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("GetCurrentUser")
            .WithTags("Auth");

        // Anonymous: the login page reads this pre-auth to decide whether to offer SSO.
        group.MapGet("/auth/sso", GetSsoConfig)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("GetSsoConfig")
            .WithTags("Auth");

        // Authenticated but not permission-gated: a user without a role gets { canRead: false } (the shell shows a
        // "no access yet" message) rather than a 403.
        group.MapGet("/auth/permissions", GetCurrentPermissions)
            .SkipBeaconPermissionCheck()
            .WithName("GetCurrentPermissions")
            .WithTags("Auth");

        return group;
    }

    private static SsoConfigResponse GetSsoConfig(IOptions<OidcAuthenticationOptions> oidcOptions)
        => new(oidcOptions.Value.Enabled);

    internal static CurrentUserResponse GetCurrentUser(
        IBeaconUserContext userContext,
        HttpContext httpContext,
        BeaconApiOptions? apiOptions)
    {
        var realtime = apiOptions?.Realtime ?? false;
        var externalLogin = ExternalLoginResponse.From(apiOptions?.ExternalLogin);

        if (!userContext.IsAuthenticated)
        {
            return CurrentUserResponse.Anonymous(realtime, externalLogin);
        }

        var roles = httpContext.User
            .FindAll(ClaimTypes.Role)
            .Select(x => x.Value)
            .Distinct()
            .ToArray();

        return new CurrentUserResponse(
            UserId: userContext.UserId,
            UserName: userContext.UserName,
            DisplayName: userContext.DisplayName,
            Email: userContext.Email,
            IsAuthenticated: true,
            Roles: roles,
            RealtimeEnabled: realtime,
            ExternalLogin: externalLogin);
    }

    private static async Task<CurrentPermissionsResponse> GetCurrentPermissions(
        IBeaconAuthorizationProvider authorization,
        CancellationToken cancellationToken)
    {
        var canRead = await authorization.HasReadPermissionAsync(cancellationToken);
        var canWrite = await authorization.HasWritePermissionAsync(cancellationToken);
        return new CurrentPermissionsResponse(canRead, canWrite);
    }
}

internal sealed record CurrentUserResponse(
    string? UserId,
    string? UserName,
    string? DisplayName,
    string? Email,
    bool IsAuthenticated,
    IReadOnlyList<string> Roles,
    bool RealtimeEnabled,
    ExternalLoginResponse? ExternalLogin)
{
    /// <summary>
    /// The unauthenticated shape. Still carries <paramref name="realtimeEnabled"/> so the shell
    /// knows whether to open a hub connection once the user signs in, and <paramref name="externalLogin"/>
    /// so it knows where to send the user to sign in — a static singleton cannot, because both are
    /// host configuration rather than constants.
    /// </summary>
    public static CurrentUserResponse Anonymous(bool realtimeEnabled, ExternalLoginResponse? externalLogin) =>
        new(null, null, null, null, false, Array.Empty<string>(), realtimeEnabled, externalLogin);
}

internal sealed record ExternalLoginResponse(string LoginUrl, string ReturnUrlParameter)
{
    public static ExternalLoginResponse? From(ExternalLoginOptions? options) =>
        options == null ? null : new ExternalLoginResponse(options.LoginUrl, options.ReturnUrlParameter);
}

internal sealed record CurrentPermissionsResponse(bool CanRead, bool CanWrite);

internal sealed record SsoConfigResponse(bool Enabled);
