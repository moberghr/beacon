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

    private static CurrentUserResponse GetCurrentUser(IBeaconUserContext userContext, HttpContext httpContext)
    {
        if (!userContext.IsAuthenticated)
        {
            return CurrentUserResponse.Anonymous;
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
            Roles: roles);
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
    IReadOnlyList<string> Roles)
{
    public static CurrentUserResponse Anonymous { get; } =
        new(null, null, null, null, false, Array.Empty<string>());
}

internal sealed record CurrentPermissionsResponse(bool CanRead, bool CanWrite);

internal sealed record SsoConfigResponse(bool Enabled);
