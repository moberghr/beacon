using Beacon.Core.Handlers.AdminSettings;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class AdminSettingsEndpoints
{
    public static RouteGroupBuilder MapAdminSettingsEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/admin-settings")
            .WithTags("AdminSettings")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        settings.MapGet("/", (IMediator m, CancellationToken ct) => m.Send(new GetAdminSettingsQuery(), ct))
            .WithName("GetAdminSettings");

        settings.MapPut("/", async (UpdateAdminSettingsCommand cmd, IMediator m, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            return TypedResults.NoContent();
        }).WithName("UpdateAdminSettings");

        settings.MapPost("/test-llm", (TestLlmConnectionCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("TestLlmConnection");

        return group;
    }
}
