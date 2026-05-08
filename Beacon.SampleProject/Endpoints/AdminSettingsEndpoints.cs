using Beacon.Core.Handlers.AdminSettings;
using MediatR;

namespace Beacon.SampleProject.Endpoints;

internal static class AdminSettingsEndpoints
{
    public static RouteGroupBuilder MapAdminSettingsEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/admin-settings")
            .WithTags("AdminSettings")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        settings.MapGet("/", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetAdminSettingsQuery(), ct)))
            .WithName("GetAdminSettings")
            .Produces<GetAdminSettingsResult>(StatusCodes.Status200OK);

        settings.MapPut("/", async (UpdateAdminSettingsCommand command, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(command, ct);
                return Results.NoContent();
            })
            .WithName("UpdateAdminSettings")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}
