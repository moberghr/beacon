using Beacon.Core.Handlers.Users;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class UsersEndpoints
{
    public static RouteGroupBuilder MapUsersEndpoints(this RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users").WithTags("Users");

        users.MapGet("/", ([FromQuery] string? search, IMediator m, CancellationToken ct) =>
                m.Send(new GetUsersQuery(search), ct))
            .WithName("GetUsers");

        users.MapGet("/roles", (IMediator m, CancellationToken ct) => m.Send(new GetRolesQuery(), ct))
            .WithName("GetRoles");

        users.MapPost("/internal", async (CreateInternalUserCommand cmd, IMediator m, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            return TypedResults.NoContent();
        }).WithName("CreateInternalUser");

        users.MapPost("/external", async (CreateExternalUserCommand cmd, IMediator m, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            return TypedResults.NoContent();
        }).WithName("CreateExternalUser");

        users.MapPut("/{id:int}", async (int id, UpdateUserBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateUserCommand(id, body.UserName, body.Email, body.DisplayName, body.IsEnabled), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateUser");

        users.MapPost("/{id:int}/toggle-enabled", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new ToggleUserEnabledCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("ToggleUserEnabled");

        return group;
    }
}

internal sealed record UpdateUserBody(
    string UserName,
    string? Email,
    string? DisplayName,
    bool IsEnabled);
