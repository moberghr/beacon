using Beacon.Api.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Beacon.Core.Authorization;
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
            .RequireBeaconPermission(BeaconPermissions.DataSourceAdminPolicy);

        // Accepts a raw connection string and dials it — an SSRF vector into internal hosts if exposed to anyone. Restricted
        // to DataSourceAdmins (and Admins), matching CreateDataSource/DeleteDataSource.
        ds.MapPost("/test-connection", (TestDataSourceConnectionCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("TestDataSourceConnection")
            .RequireBeaconPermission(BeaconPermissions.DataSourceAdminPolicy);

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
        }).WithName("DeleteDataSource").RequireBeaconPermission(BeaconPermissions.DataSourceAdminPolicy);

        // Marks a data source as a migration write target (or returns it to a read source). Admin only: a write target may
        // carry a login with write grants. Mapped whether or not migrations run, so a target can always be unmarked. A source
        // that queries, subscriptions or data contracts read answers 409 with how many, unless the body confirms (force).
        ds.MapPut("/{id:int}/migration-writes", async (int id, SetDataSourceMigrationWritesBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new SetDataSourceMigrationWritesCommand(id, body.AllowMigrationWrites, body.Force), ct);
            return TypedResults.NoContent();
        }).WithName("SetDataSourceMigrationWrites").RequireBeaconAdmin();

        return group;
    }
}

/// <summary>
/// Whether the data source is a migration write target; <paramref name="Force"/> confirms marking one that queries,
/// subscriptions or data contracts read.
/// </summary>
internal sealed record SetDataSourceMigrationWritesBody(bool AllowMigrationWrites, bool Force = false);
