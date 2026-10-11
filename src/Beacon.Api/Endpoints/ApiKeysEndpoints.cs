using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.ApiKeys;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class ApiKeysEndpoints
{
    public static RouteGroupBuilder MapApiKeysEndpoints(this RouteGroupBuilder group)
    {
        // Keys are managed from a signed-in session only: an API key, an MCP caller or a bearer token can neither list,
        // create nor revoke keys (the handlers refuse them too).
        var keys = group.MapGroup("/api-keys")
            .WithTags("ApiKeys")
            .AddEndpointFilter(RequireInteractiveSessionAsync);

        keys.MapGet("/", ([AsParameters] GetApiKeysQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetApiKeys");

        // Viewers may issue Read keys and revoke their own keys; the handler requires write permission for an
        // Execute key.
        keys.MapPost("/", (CreateApiKeyCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("CreateApiKey")
            .AllowViewerAccess();

        keys.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new RevokeApiKeyCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("RevokeApiKey").AllowViewerAccess();

        // Administration of every user's keys: an Auditor or a UserAdmin lists them (optionally one user's), a UserAdmin
        // revokes any key.
        var admin = keys.MapGroup("/admin");

        admin.MapGet("/", ([AsParameters] GetAllApiKeysQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetAllApiKeys")
            .RequireBeaconPermission(BeaconPermissions.AuditorOrUserAdminPolicy);

        admin.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new AdminRevokeApiKeyCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("AdminRevokeApiKey").RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        return group;
    }

    private static async ValueTask<object?> RequireInteractiveSessionAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (!ApiKeyManagementCaller.IsInteractiveSession(context.HttpContext.User))
        {
            return Results.Problem(
                title: "Forbidden",
                detail: ApiKeyManagementCaller.InteractiveSessionRequired,
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
