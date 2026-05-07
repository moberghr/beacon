using Beacon.Core.Handlers.QueryFolders;
using MediatR;

namespace Beacon.SampleProject.Endpoints;

internal static class QueryFoldersEndpoints
{
    public static RouteGroupBuilder MapQueryFoldersEndpoints(this RouteGroupBuilder group)
    {
        var folders = group.MapGroup("/query-folders").WithTags("QueryFolders");

        folders.MapGet("/", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetQueryFoldersQuery(), ct)))
            .WithName("GetQueryFolders")
            .Produces<GetQueryFoldersResult>(StatusCodes.Status200OK);

        folders.MapPost("/", async (CreateQueryFolderCommand command, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(command, ct);
                return Results.Created($"/beacon/api/query-folders/{result.FolderId}", result);
            })
            .WithName("CreateQueryFolder")
            .Produces<CreateQueryFolderResult>(StatusCodes.Status201Created);

        folders.MapPut("/{id:int}", async (
                int id,
                UpdateQueryFolderRequest body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new UpdateQueryFolderCommand(id, body.Name, body.Description), ct);
                return Results.NoContent();
            })
            .WithName("UpdateQueryFolder")
            .Produces(StatusCodes.Status204NoContent);

        folders.MapDelete("/{id:int}", async (
                int id,
                bool? moveQueriesToParent,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new DeleteQueryFolderCommand(id, moveQueriesToParent ?? false), ct);
                return Results.NoContent();
            })
            .WithName("DeleteQueryFolder")
            .Produces(StatusCodes.Status204NoContent);

        // /queries/{id}/move belongs with query operations but the command lives in QueryFolders.
        var queries = group.MapGroup("/queries").WithTags("Queries");

        queries.MapPut("/{id:int}/folder", async (
                int id,
                MoveQueryToFolderRequest body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new MoveQueryToFolderCommand(id, body.FolderId), ct);
                return Results.NoContent();
            })
            .WithName("MoveQueryToFolder")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}

internal sealed record UpdateQueryFolderRequest(string Name, string? Description);
internal sealed record MoveQueryToFolderRequest(int? FolderId);
