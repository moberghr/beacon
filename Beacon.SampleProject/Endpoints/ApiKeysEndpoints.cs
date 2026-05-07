using Beacon.Core.Handlers.ApiKeys;
using MediatR;

namespace Beacon.SampleProject.Endpoints;

internal static class ApiKeysEndpoints
{
    public static RouteGroupBuilder MapApiKeysEndpoints(this RouteGroupBuilder group)
    {
        var keys = group.MapGroup("/api-keys").WithTags("ApiKeys");

        keys.MapGet("/", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetApiKeysQuery(), ct)))
            .WithName("GetApiKeys")
            .Produces<GetApiKeysResult>(StatusCodes.Status200OK);

        keys.MapPost("/", async (CreateApiKeyCommand command, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateApiKey")
            .Produces<CreateApiKeyResult>(StatusCodes.Status200OK);

        keys.MapDelete("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new RevokeApiKeyCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("RevokeApiKey")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}
