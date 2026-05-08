using Beacon.Core.Handlers.DataSources;
using MediatR;

namespace Beacon.SampleProject.Endpoints;

internal static class DataSourcesEndpoints
{
    public static RouteGroupBuilder MapDataSourcesEndpoints(this RouteGroupBuilder group)
    {
        var ds = group.MapGroup("/data-sources").WithTags("DataSources");

        ds.MapGet("/", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetDataSourcesQuery(), ct)))
            .WithName("GetDataSources")
            .Produces<GetDataSourcesResult>(StatusCodes.Status200OK);

        ds.MapPost("/", async (CreateDataSourceCommand command, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateDataSource")
            .Produces<CreateDataSourceResult>(StatusCodes.Status200OK);

        ds.MapPost("/test-connection", async (TestDataSourceConnectionCommand command, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(command, ct)))
            .WithName("TestDataSourceConnection")
            .Produces<TestDataSourceConnectionResult>(StatusCodes.Status200OK);

        ds.MapDelete("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteDataSourceCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("DeleteDataSource")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}
