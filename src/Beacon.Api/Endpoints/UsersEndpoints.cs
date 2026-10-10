using Beacon.Core.Handlers.Users;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class UsersEndpoints
{
    public static RouteGroupBuilder MapUsersEndpoints(this RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users").WithTags("Users");

        // The user directory and the role list are an Admin's, like every user change below.
        users.MapGet("/", ([AsParameters] GetUsersQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetUsers")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        users.MapGet("/roles", (IMediator m, CancellationToken ct) => m.Send(new GetRolesQuery(), ct))
            .WithName("GetRoles")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        users.MapPost("/internal", async (CreateInternalUserCommand cmd, IMediator m, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            return TypedResults.NoContent();
        }).WithName("CreateInternalUser").RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        users.MapPost("/external", async (CreateExternalUserCommand cmd, IMediator m, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            return TypedResults.NoContent();
        }).WithName("CreateExternalUser").RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        users.MapPut("/{id:int}", async (int id, UpdateUserBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateUserCommand(id, body.UserName, body.Email, body.DisplayName, body.IsEnabled), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateUser").RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        users.MapPost("/{id:int}/toggle-enabled", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new ToggleUserEnabledCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("ToggleUserEnabled").RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        return group;
    }
}

internal sealed record UpdateUserBody(
    string UserName,
    string? Email,
    string? DisplayName,
    bool IsEnabled);
