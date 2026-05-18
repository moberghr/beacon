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

namespace Beacon.Api.Endpoints;

internal static class DataQualityEndpoints
{
    public static RouteGroupBuilder MapDataQualityEndpoints(this RouteGroupBuilder group)
    {
        var quality = group.MapGroup("/data-quality").WithTags("DataQuality");

        quality.MapGet("/overview", ([FromQuery] int? dataSourceId, IMediator m, CancellationToken ct) =>
                m.Send(new GetDataQualityOverviewQuery(dataSourceId), ct))
            .WithName("GetDataQualityOverview");

        quality.MapGet("/contracts", ([FromQuery] int? dataSourceId, IMediator m, CancellationToken ct) =>
                m.Send(new GetDataContractsQuery(dataSourceId), ct))
            .WithName("GetDataContracts");

        quality.MapGet("/contracts/{id:int}", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetDataContractDetailQuery(id), ct))
            .WithName("GetDataContractDetail");

        quality.MapPost("/contracts", (CreateDataContractCommand cmd, IMediator m, CancellationToken ct) =>
                m.Send(cmd, ct))
            .WithName("CreateDataContract");

        quality.MapPut("/contracts/{id:int}", async (int id, UpdateDataContractBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateDataContractCommand(
                id, body.DataSourceId, body.SchemaName, body.TableName, body.Name,
                body.Description, body.CronExpression, body.IsEnabled, body.OwnerUserId,
                body.AlertOnFailure, body.FailureThresholdScore, body.Rules, body.RecipientIds), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateDataContract");

        quality.MapDelete("/contracts/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteDataContractCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteDataContract");

        quality.MapGet("/contracts/{id:int}/evaluations", (int id, [FromQuery] int? take, IMediator m, CancellationToken ct) =>
                m.Send(new GetEvaluationHistoryQuery(id, take), ct))
            .WithName("GetEvaluationHistory");

        quality.MapPost("/contracts/{id:int}/evaluate", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new EvaluateDataContractCommand(id), ct))
            .WithName("EvaluateDataContract");

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
