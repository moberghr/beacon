using Beacon.Core.Handlers.ApiKeys;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class ApiKeysEndpoints
{
    public static RouteGroupBuilder MapApiKeysEndpoints(this RouteGroupBuilder group)
    {
        var keys = group.MapGroup("/api-keys").WithTags("ApiKeys");

        keys.MapGet("/", (IMediator m, CancellationToken ct) => m.Send(new GetApiKeysQuery(), ct))
            .WithName("GetApiKeys");

        keys.MapPost("/", (CreateApiKeyCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("CreateApiKey");

        keys.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new RevokeApiKeyCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("RevokeApiKey");

        return group;
    }
}
