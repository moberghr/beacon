using Beacon.Api.Authentication;
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

        // Every signed-in user (Viewers included) may change their own password. The new password signs out every session
        // of the user; this one is signed in again so it carries the new security stamp.
        settings.MapPost("/change-password", async (ChangeOwnPasswordCommand cmd, IMediator m, HttpContext context, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            await BeaconSessionEvents.RefreshSignInAsync(context);
            return TypedResults.NoContent();
        })
        .WithName("ChangeOwnPassword")
        .AllowViewerAccess();

        return group;
    }
}
