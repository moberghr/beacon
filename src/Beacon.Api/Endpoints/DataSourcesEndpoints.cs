using Beacon.Api.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Enums;
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

        // Refreshes in the request and answers with the catalog (kept for 5.0 callers; a large catalog holds the request open).
        ds.MapPost("/{id:int}/refresh-metadata", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new RefreshDataSourceMetadataCommand(id), ct))
            .WithName("RefreshDataSourceMetadata");

        // Starts the refresh as a background task: 202 with the task (its job id correlates JobStatusChanged), read below.
        ds.MapPost("/{id:int}/metadata-refreshes", async (int id, IMediator m, CancellationToken ct) =>
            {
                var ticket = await m.Send(new StartDataSourceMetadataRefreshCommand(id), ct);
                return TypedResults.Accepted($"/beacon/api/data-sources/{id}/metadata-refreshes/{ticket.TaskId}", ticket);
            })
            .WithName("StartDataSourceMetadataRefresh");

        ds.MapGet("/{id:int}/metadata-refreshes/{taskId:int}", async Task<Results<Ok<BackgroundTaskStatus>, NotFound>> (int id, int taskId, IMediator m, CancellationToken ct) =>
            {
                var status = await m.Send(new GetDataSourceMetadataRefreshQuery(id, taskId), ct);
                return status is null ? TypedResults.NotFound() : TypedResults.Ok(status);
            })
            .WithName("GetDataSourceMetadataRefresh");

        // Edits the source and replaces its connection (credential rotation). The stored connection is never sent: an omitted
        // connection keeps it. A connection that points somewhere else answers 409 unless the body confirms the change.
        ds.MapPut("/{id:int}", async (int id, UpdateDataSourceBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(
                new UpdateDataSourceCommand(
                    id,
                    body.Name,
                    body.DatabaseEngineType,
                    body.ConnectionString,
                    body.MetadataLoadingEnabled,
                    body.MetadataMaxTables,
                    body.MetadataMaxColumnsPerTable,
                    body.MetadataLoadTableNamesOnly,
                    body.MetadataExcludeSchemas,
                    body.MetadataIncludeSchemas,
                    body.ConfirmTargetChange),
                ct);
            return TypedResults.NoContent();
        }).WithName("UpdateDataSource").RequireBeaconPermission(BeaconPermissions.DataSourceAdminPolicy);

        // Disables a source (no connection is made with it) or enables it again, at once.
        ds.MapPut("/{id:int}/enabled", async (int id, SetDataSourceEnabledBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new SetDataSourceEnabledCommand(id, body.Enabled), ct);
            return TypedResults.NoContent();
        }).WithName("SetDataSourceEnabled").RequireBeaconPermission(BeaconPermissions.DataSourceAdminPolicy);

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

/// <summary>
/// A data source's editable settings. <paramref name="ConnectionString"/> null or empty keeps the stored connection;
/// <paramref name="ConfirmTargetChange"/> confirms a connection to another engine, host, port or database. A metadata option
/// left out (null) keeps the stored value; an empty schema list clears it.
/// </summary>
internal sealed record UpdateDataSourceBody(
    string Name,
    DatabaseEngineType? DatabaseEngineType,
    string? ConnectionString,
    bool? MetadataLoadingEnabled = null,
    int? MetadataMaxTables = null,
    int? MetadataMaxColumnsPerTable = null,
    bool? MetadataLoadTableNamesOnly = null,
    List<string>? MetadataExcludeSchemas = null,
    List<string>? MetadataIncludeSchemas = null,
    bool ConfirmTargetChange = false);

/// <summary>Whether the data source is enabled: false disables it, so Beacon makes no connection with it.</summary>
internal sealed record SetDataSourceEnabledBody(bool Enabled);
