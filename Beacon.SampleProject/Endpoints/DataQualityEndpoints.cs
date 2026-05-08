using Beacon.Core.Handlers.DataCatalog;
using Beacon.Core.Handlers.DataQuality.CreateDataContract;
using Beacon.Core.Handlers.DataQuality.DeleteDataContract;
using Beacon.Core.Handlers.DataQuality.EvaluateDataContract;
using Beacon.Core.Handlers.DataQuality.GetDataContractDetail;
using Beacon.Core.Handlers.DataQuality.GetDataContracts;
using Beacon.Core.Handlers.DataQuality.GetDataQualityOverview;
using Beacon.Core.Handlers.DataQuality.GetEvaluationHistory;
using Beacon.Core.Handlers.DataQuality.UpdateDataContract;
using Beacon.Core.Models.DataQuality;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class DataQualityEndpoints
{
    public static RouteGroupBuilder MapDataQualityEndpoints(this RouteGroupBuilder group)
    {
        var quality = group.MapGroup("/data-quality").WithTags("DataQuality");

        quality.MapGet("/overview", async ([FromQuery] int? dataSourceId, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetDataQualityOverviewQuery(dataSourceId), ct)))
            .WithName("GetDataQualityOverview")
            .Produces<List<DataQualityOverviewData>>(StatusCodes.Status200OK);

        quality.MapGet("/contracts", async ([FromQuery] int? dataSourceId, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetDataContractsQuery(dataSourceId), ct)))
            .WithName("GetDataContracts")
            .Produces<List<DataContractData>>(StatusCodes.Status200OK);

        quality.MapGet("/contracts/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetDataContractDetailQuery(id), ct)))
            .WithName("GetDataContractDetail")
            .Produces<DataContractData>(StatusCodes.Status200OK);

        quality.MapPost("/contracts", async (CreateDataContractCommand command, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(command, ct);
                return Results.Ok(result);
            })
            .WithName("CreateDataContract")
            .Produces<CreateDataContractResult>(StatusCodes.Status200OK);

        quality.MapPut("/contracts/{id:int}", async (int id, UpdateDataContractBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateDataContractCommand(
                    id, body.DataSourceId, body.SchemaName, body.TableName, body.Name,
                    body.Description, body.CronExpression, body.IsEnabled, body.OwnerUserId,
                    body.AlertOnFailure, body.FailureThresholdScore, body.Rules, body.RecipientIds), ct);
                return Results.NoContent();
            })
            .WithName("UpdateDataContract")
            .Produces(StatusCodes.Status204NoContent);

        quality.MapDelete("/contracts/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteDataContractCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("DeleteDataContract")
            .Produces(StatusCodes.Status204NoContent);

        quality.MapGet("/contracts/{id:int}/evaluations", async (int id, [FromQuery] int? take, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetEvaluationHistoryQuery(id, take), ct)))
            .WithName("GetEvaluationHistory")
            .Produces<GetEvaluationHistoryResult>(StatusCodes.Status200OK);

        quality.MapPost("/contracts/{id:int}/evaluate", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new EvaluateDataContractCommand(id), ct)))
            .WithName("EvaluateDataContract")
            .Produces<DataQualityEvaluationData>(StatusCodes.Status200OK);

        // Data catalog (single endpoint)
        group.MapGet("/data-catalog", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetDataCatalogQuery(), ct)))
            .WithName("GetDataCatalog")
            .WithTags("DataCatalog")
            .Produces<GetDataCatalogResult>(StatusCodes.Status200OK);

        return group;
    }
}

internal sealed record UpdateDataContractBody(
    int DataSourceId,
    string SchemaName,
    string TableName,
    string Name,
    string? Description,
    string CronExpression,
    bool IsEnabled,
    string? OwnerUserId,
    bool AlertOnFailure,
    int FailureThresholdScore,
    List<DataContractRuleData> Rules,
    List<int>? RecipientIds);
