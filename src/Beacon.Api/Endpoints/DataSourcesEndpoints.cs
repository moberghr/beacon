using Beacon.Api.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Beacon.Core.Handlers.DataSources;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class DataSourcesEndpoints
{
    public static RouteGroupBuilder MapDataSourcesEndpoints(this RouteGroupBuilder group)
    {
        var ds = group.MapGroup("/data-sources").WithTags("DataSources");

        ds.MapGet("/", ([AsParameters] GetDataSourcesQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetDataSources");

        ds.MapGet("/{id:int}", async Task<Results<Ok<DataSourceEntry>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
            {
                var dataSource = await m.Send(new GetDataSourceQuery(id), ct);
                return dataSource is null ? TypedResults.NotFound() : TypedResults.Ok(dataSource);
            })
            .WithName("GetDataSource");

        ds.MapPost("/", (CreateDataSourceCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("CreateDataSource")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        // Accepts a raw connection string and dials it — an SSRF vector into internal hosts if
        // exposed to non-admins. Restricted to admins, matching CreateDataSource/DeleteDataSource.
        ds.MapPost("/test-connection", (TestDataSourceConnectionCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("TestDataSourceConnection")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        // Scans the data source live when no metadata is stored yet, so a scoped caller needs the Execute scope.
        ds.MapGet("/{id:int}/metadata", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetDataSourceMetadataQuery(id), ct))
            .WithName("GetDataSourceMetadata")
            .RequiresExecuteScope();

        ds.MapPost("/{id:int}/refresh-metadata", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new RefreshDataSourceMetadataCommand(id), ct))
            .WithName("RefreshDataSourceMetadata");

        ds.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteDataSourceCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteDataSource").RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        return group;
    }
}
