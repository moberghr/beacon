using Beacon.Core.Handlers.UserSettings;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class UserSettingsEndpoints
{
    public static RouteGroupBuilder MapUserSettingsEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/user-settings").WithTags("UserSettings");

        settings.MapGet("/", (IMediator m, CancellationToken ct) => m.Send(new GetUserSettingsQuery(), ct))
            .WithName("GetUserSettings");

        settings.MapPost("/change-password", async (ChangeOwnPasswordCommand cmd, IMediator m, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            return TypedResults.NoContent();
        }).WithName("ChangeOwnPassword");

        return group;
    }
}
