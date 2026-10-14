using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.AdminSettings;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class AdminSettingsEndpoints
{
    public static RouteGroupBuilder MapAdminSettingsEndpoints(this RouteGroupBuilder group)
    {
        // An Auditor or a SecurityAdmin reads the settings (secrets masked); a SecurityAdmin changes and tests them, except
        // the LLM provider, endpoint, region and credentials, which stay an Admin's (checked by the handlers).
        var settings = group.MapGroup("/admin-settings")
            .WithTags("AdminSettings");

        settings.MapGet("/", (IMediator m, CancellationToken ct) => m.Send(new GetAdminSettingsQuery(), ct))
            .WithName("GetAdminSettings")
            .RequireBeaconPermission(BeaconPermissions.AuditorOrSecurityAdminPolicy);

        settings.MapPut("/", async (UpdateAdminSettingsCommand cmd, IMediator m, CancellationToken ct) =>
        {
            await m.Send(cmd, ct);
            return TypedResults.NoContent();
        }).WithName("UpdateAdminSettings").RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        settings.MapPost("/test-llm", (TestLlmConnectionCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("TestLlmConnection")
            .RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        return group;
    }
}
