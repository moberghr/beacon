using System.Security.Claims;
using Beacon.Core.Authorization;

namespace Beacon.SampleProject.Endpoints;

internal static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/auth/me", GetCurrentUser)
            .AllowAnonymous()
            .WithName("GetCurrentUser")
            .WithTags("Auth")
            .Produces<CurrentUserResponse>(StatusCodes.Status200OK);

        group.MapGet("/auth/permissions", GetCurrentPermissions)
            .RequireAuthorization()
            .WithName("GetCurrentPermissions")
            .WithTags("Auth")
            .Produces<CurrentPermissionsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return group;
    }

    private static IResult GetCurrentUser(IBeaconUserContext userContext, HttpContext httpContext)
    {
        if (!userContext.IsAuthenticated)
        {
            return Results.Ok(CurrentUserResponse.Anonymous);
        }

        var roles = httpContext.User
            .FindAll(ClaimTypes.Role)
            .Select(x => x.Value)
            .Distinct()
            .ToArray();

        var response = new CurrentUserResponse(
            UserId: userContext.UserId,
            UserName: userContext.UserName,
            DisplayName: userContext.DisplayName,
            Email: userContext.Email,
            IsAuthenticated: true,
            Roles: roles);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetCurrentPermissions(
        IBeaconAuthorizationProvider authorization,
        CancellationToken cancellationToken)
    {
        var canRead = await authorization.HasReadPermissionAsync(cancellationToken);
        var canWrite = await authorization.HasWritePermissionAsync(cancellationToken);

        return Results.Ok(new CurrentPermissionsResponse(canRead, canWrite));
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
