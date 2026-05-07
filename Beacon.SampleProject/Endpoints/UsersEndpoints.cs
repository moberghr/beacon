using Beacon.Core.Handlers.Users;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class UsersEndpoints
{
    public static RouteGroupBuilder MapUsersEndpoints(this RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users").WithTags("Users");

        users.MapGet("/", async (
                [FromQuery] string? search,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetUsersQuery(search), ct)))
            .WithName("GetUsers")
            .Produces<GetUsersResult>(StatusCodes.Status200OK);

        users.MapGet("/roles", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetRolesQuery(), ct)))
            .WithName("GetRoles")
            .Produces<GetRolesResult>(StatusCodes.Status200OK);

        users.MapPost("/internal", async (CreateInternalUserCommand command, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(command, ct);
                return Results.NoContent();
            })
            .WithName("CreateInternalUser")
            .Produces(StatusCodes.Status204NoContent);

        users.MapPost("/external", async (CreateExternalUserCommand command, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(command, ct);
                return Results.NoContent();
            })
            .WithName("CreateExternalUser")
            .Produces(StatusCodes.Status204NoContent);

        users.MapPut("/{id:int}", async (int id, UpdateUserBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateUserCommand(
                    id,
                    body.UserName,
                    body.Email,
                    body.DisplayName,
                    body.IsEnabled), ct);
                return Results.NoContent();
            })
            .WithName("UpdateUser")
            .Produces(StatusCodes.Status204NoContent);

        users.MapPost("/{id:int}/toggle-enabled", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new ToggleUserEnabledCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("ToggleUserEnabled")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}

internal sealed record UpdateUserBody(
    string UserName,
    string? Email,
    string? DisplayName,
    bool IsEnabled);
