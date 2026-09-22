using System.Security.Claims;
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

        group.MapGet("/auth/permissions", GetCurrentPermissions)
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
        if (!userContext.IsAuthenticated)
        {
            return CurrentUserResponse.Anonymous(apiOptions?.Realtime ?? false);
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
            RealtimeEnabled: apiOptions?.Realtime ?? false);
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
    bool RealtimeEnabled)
{
    /// <summary>
    /// The unauthenticated shape. Still carries <paramref name="realtimeEnabled"/> so the shell
    /// knows whether to open a hub connection once the user signs in — a static singleton cannot,
    /// because the value is host configuration rather than a constant.
    /// </summary>
    public static CurrentUserResponse Anonymous(bool realtimeEnabled) =>
        new(null, null, null, null, false, Array.Empty<string>(), realtimeEnabled);
}

internal sealed record CurrentPermissionsResponse(bool CanRead, bool CanWrite);

internal sealed record SsoConfigResponse(bool Enabled);
