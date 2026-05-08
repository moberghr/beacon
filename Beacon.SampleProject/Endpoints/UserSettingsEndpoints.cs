using Beacon.Core.Handlers.UserSettings;
using MediatR;

namespace Beacon.SampleProject.Endpoints;

internal static class UserSettingsEndpoints
{
    public static RouteGroupBuilder MapUserSettingsEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/user-settings")
            .WithTags("UserSettings");

        settings.MapGet("/", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetUserSettingsQuery(), ct)))
            .WithName("GetUserSettings")
            .Produces<GetUserSettingsResult>(StatusCodes.Status200OK);

        settings.MapPost("/change-password", async (ChangeOwnPasswordCommand command, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(command, ct);
                return Results.NoContent();
            })
            .WithName("ChangeOwnPassword")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}
