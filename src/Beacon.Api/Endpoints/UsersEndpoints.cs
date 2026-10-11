using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Users;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class UsersEndpoints
{
    public static RouteGroupBuilder MapUsersEndpoints(this RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users").WithTags("Users");

        // The user directory and the role list are a UserAdmin's (and an Admin's), like every user change below.
        users.MapGet("/", ([AsParameters] GetUsersQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetUsers")
            .RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        users.MapGet("/roles", (IMediator m, CancellationToken ct) => m.Send(new GetRolesQuery(), ct))
            .WithName("GetRoles")
            .RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        users.MapPost("/internal", async (CreateInternalUserCommand cmd, IMediator m, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            return TypedResults.NoContent();
        }).WithName("CreateInternalUser").RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        users.MapPost("/external", async (CreateExternalUserCommand cmd, IMediator m, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            return TypedResults.NoContent();
        }).WithName("CreateExternalUser").RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        users.MapPut("/{id:int}", async (int id, UpdateUserBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateUserCommand(id, body.UserName, body.Email, body.DisplayName, body.IsEnabled), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateUser").RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        users.MapPost("/{id:int}/toggle-enabled", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new ToggleUserEnabledCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("ToggleUserEnabled").RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        // Replaces the user's roles. Only an Admin assigns or removes the Admin role or a functional role.
        users.MapPut("/{id:int}/roles", async (int id, SetUserRolesBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new SetUserRolesCommand(id, body.RoleIds), ct);
            return TypedResults.NoContent();
        }).WithName("SetUserRoles").RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        return group;
    }
}

internal sealed record SetUserRolesBody(int[] RoleIds);

internal sealed record UpdateUserBody(
    string UserName,
    string? Email,
    string? DisplayName,
    bool IsEnabled);
