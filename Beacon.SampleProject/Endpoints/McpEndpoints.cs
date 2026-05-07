using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class McpEndpoints
{
    public static RouteGroupBuilder MapMcpManagementEndpoints(this RouteGroupBuilder group)
    {
        var mcp = group.MapGroup("/mcp").WithTags("Mcp");

        mcp.MapGet("/settings", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetMcpSettingsQuery(), ct)))
            .WithName("GetMcpSettings")
            .Produces<McpSettingsData>(StatusCodes.Status200OK);

        mcp.MapPut("/settings", async (UpdateMcpSettingsBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateMcpSettingsCommand(body.Data), ct);
                return Results.NoContent();
            })
            .WithName("UpdateMcpSettings")
            .Produces(StatusCodes.Status204NoContent);

        mcp.MapGet("/learned-patterns", async (
                [FromQuery] int? projectId,
                [FromQuery] int? dataSourceId,
                [FromQuery] McpPatternStatus? status,
                [FromQuery] McpPatternType? patternType,
                [FromQuery] string? tableName,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetLearnedPatternsQuery
                {
                    ProjectId = projectId,
                    DataSourceId = dataSourceId,
                    Status = status,
                    PatternType = patternType,
                    TableName = tableName,
                }, ct)))
            .WithName("GetLearnedPatterns")
            .Produces<GetLearnedPatternsResult>(StatusCodes.Status200OK);

        mcp.MapPut("/learned-patterns/{id:int}/status", async (
                int id,
                UpdatePatternStatusBody body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new UpdatePatternStatusCommand
                {
                    PatternId = id,
                    NewStatus = body.NewStatus,
                    ReviewedByUserId = body.ReviewedByUserId,
                }, ct);
                return Results.NoContent();
            })
            .WithName("UpdatePatternStatus")
            .Produces(StatusCodes.Status204NoContent);

        mcp.MapGet("/documentation-patches", async (
                [FromQuery] int? projectId,
                [FromQuery] McpDocPatchStatus? status,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetDocumentationPatchesQuery
                {
                    ProjectId = projectId,
                    Status = status,
                }, ct)))
            .WithName("GetDocumentationPatches")
            .Produces<GetDocumentationPatchesResult>(StatusCodes.Status200OK);

        mcp.MapPost("/documentation-patches/{id:int}/apply", async (
                int id,
                ApplyRejectPatchBody body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new ApplyDocumentationPatchCommand
                {
                    PatchId = id,
                    AppliedByUserId = body.UserId,
                }, ct);
                return Results.NoContent();
            })
            .WithName("ApplyDocumentationPatch")
            .Produces(StatusCodes.Status204NoContent);

        mcp.MapPost("/documentation-patches/{id:int}/reject", async (
                int id,
                ApplyRejectPatchBody body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new RejectDocumentationPatchCommand
                {
                    PatchId = id,
                    RejectedByUserId = body.UserId,
                }, ct);
                return Results.NoContent();
            })
            .WithName("RejectDocumentationPatch")
            .Produces(StatusCodes.Status204NoContent);

        mcp.MapGet("/learning-stats", async (
                [FromQuery] int? projectId,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetLearningStatsQuery { ProjectId = projectId }, ct)))
            .WithName("GetLearningStats")
            .Produces<LearningStatsResult>(StatusCodes.Status200OK);

        return group;
    }
}

internal sealed record UpdateMcpSettingsBody(McpSettingsData Data);
internal sealed record UpdatePatternStatusBody(McpPatternStatus NewStatus, int? ReviewedByUserId);
internal sealed record ApplyRejectPatchBody(int? UserId);
