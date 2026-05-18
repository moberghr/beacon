using Beacon.Core.Handlers.QueryFolders;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class QueryFoldersEndpoints
{
    public static RouteGroupBuilder MapQueryFoldersEndpoints(this RouteGroupBuilder group)
    {
        var folders = group.MapGroup("/query-folders").WithTags("QueryFolders");

        folders.MapGet("/", (IMediator m, CancellationToken ct) => m.Send(new GetQueryFoldersQuery(), ct))
            .WithName("GetQueryFolders");

        folders.MapPost("/", async (CreateQueryFolderCommand cmd, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(cmd, ct);
            return TypedResults.Created($"/beacon/api/query-folders/{result.FolderId}", result);
        }).WithName("CreateQueryFolder");

        folders.MapPut("/{id:int}", async (int id, UpdateQueryFolderRequest body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateQueryFolderCommand(id, body.Name, body.Description), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateQueryFolder");

        folders.MapDelete("/{id:int}", async (int id, bool? moveQueriesToParent, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteQueryFolderCommand(id, moveQueriesToParent ?? false), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteQueryFolder");

        // /queries/{id}/move belongs with query operations but the command lives in QueryFolders.
        var queries = group.MapGroup("/queries").WithTags("Queries");

        queries.MapPut("/{id:int}/folder", async (int id, MoveQueryToFolderRequest body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new MoveQueryToFolderCommand(id, body.FolderId), ct);
            return TypedResults.NoContent();
        }).WithName("MoveQueryToFolder");

        return group;
    }
}

internal sealed record UpdateQueryFolderRequest(string Name, string? Description);
internal sealed record MoveQueryToFolderRequest(int? FolderId);
