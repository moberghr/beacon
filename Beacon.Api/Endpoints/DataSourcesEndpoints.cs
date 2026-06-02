using Beacon.Core.Handlers.DataSources;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class DataSourcesEndpoints
{
    public static RouteGroupBuilder MapDataSourcesEndpoints(this RouteGroupBuilder group)
    {
        var ds = group.MapGroup("/data-sources").WithTags("DataSources");

        ds.MapGet("/", (IMediator m, CancellationToken ct) => m.Send(new GetDataSourcesQuery(), ct))
            .WithName("GetDataSources");

        ds.MapPost("/", (CreateDataSourceCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("CreateDataSource");

        ds.MapPost("/test-connection", (TestDataSourceConnectionCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("TestDataSourceConnection");

        ds.MapGet("/{id:int}/metadata", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetDataSourceMetadataQuery(id), ct))
            .WithName("GetDataSourceMetadata");

        ds.MapPost("/{id:int}/refresh-metadata", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new RefreshDataSourceMetadataCommand(id), ct))
            .WithName("RefreshDataSourceMetadata");

        ds.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteDataSourceCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteDataSource");

        return group;
    }
}
