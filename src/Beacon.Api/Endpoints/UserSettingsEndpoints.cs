using Beacon.Api.Authentication;
using Beacon.Core.Handlers.UserSettings;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Beacon.Api.Endpoints;

internal static class UserSettingsEndpoints
{
    internal const string SignInAgainDetail =
        "Your password was changed, and this session was signed out at the same time. Sign in again with your new password.";

    public static RouteGroupBuilder MapUserSettingsEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/user-settings").WithTags("UserSettings");

        settings.MapGet("/", (IMediator m, CancellationToken ct) => m.Send(new GetUserSettingsQuery(), ct))
            .WithName("GetUserSettings");

        // Every signed-in user (Viewers included) may change their own password. The new password signs out every session
        // of the user; this one is signed in again so it carries the new security stamp. When the stamp changed again
        // meanwhile (a sign-out everywhere, a reset), this session is signed out too: 401, and the user signs in again.
        settings.MapPost("/change-password", async Task<Results<NoContent, ProblemHttpResult>> (
            ChangeOwnPasswordCommand cmd,
            IMediator m,
            HttpContext context,
            CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            await BeaconSessionEvents.RefreshSignInAsync(context);
            if (BeaconSessionEvents.WasSignInRefused(context))
            {
                return TypedResults.Problem(
                    title: "Unauthorized",
                    detail: SignInAgainDetail,
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return TypedResults.NoContent();
        })
        .WithName("ChangeOwnPassword")
        .AllowViewerAccess();

        return group;
    }
}
