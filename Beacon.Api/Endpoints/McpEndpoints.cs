using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Mcp.RunMcpTool;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class McpEndpoints
{
    public static RouteGroupBuilder MapMcpManagementEndpoints(this RouteGroupBuilder group)
    {
        var mcp = group.MapGroup("/mcp").WithTags("Mcp");

        mcp.MapGet("/settings", (IMediator m, CancellationToken ct) => m.Send(new GetMcpSettingsQuery(), ct))
            .WithName("GetMcpSettings");

        mcp.MapPut("/settings", async (UpdateMcpSettingsBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateMcpSettingsCommand(body.Data), ct);
            return TypedResults.NoContent();
        })
        .WithName("UpdateMcpSettings")
        .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        mcp.MapGet("/learned-patterns", (
                [FromQuery] int? projectId,
                [FromQuery] int? dataSourceId,
                [FromQuery] McpPatternStatus? status,
                [FromQuery] McpPatternType? patternType,
                [FromQuery] string? tableName,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new GetLearnedPatternsQuery
                {
                    ProjectId = projectId,
                    DataSourceId = dataSourceId,
                    Status = status,
                    PatternType = patternType,
                    TableName = tableName,
                }, ct))
            .WithName("GetLearnedPatterns");

        mcp.MapPut("/learned-patterns/{id:int}/status", async (
            int id,
            UpdatePatternStatusBody body,
            IMediator m,
            CancellationToken ct) =>
        {
            // Reviewer id is not yet derivable from claims (BeaconUser.Id is int,
            // ClaimTypes.NameIdentifier is a string ExternalId). Ignore body value
            // to prevent impersonation; surface as unknown until claim→userId
            // resolution lands.
            await m.Send(new UpdatePatternStatusCommand
            {
                PatternId = id,
                NewStatus = body.NewStatus,
                ReviewedByUserId = null,
            }, ct);
            return TypedResults.NoContent();
        }).WithName("UpdatePatternStatus");

        mcp.MapGet("/documentation-patches", (
                [FromQuery] int? projectId,
                [FromQuery] McpDocPatchStatus? status,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new GetDocumentationPatchesQuery
                {
                    ProjectId = projectId,
                    Status = status,
                }, ct))
            .WithName("GetDocumentationPatches");

        mcp.MapPost("/documentation-patches/{id:int}/apply", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new ApplyDocumentationPatchCommand { PatchId = id, AppliedByUserId = null }, ct);
            return TypedResults.NoContent();
        }).WithName("ApplyDocumentationPatch");

        mcp.MapPost("/documentation-patches/{id:int}/reject", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new RejectDocumentationPatchCommand { PatchId = id, RejectedByUserId = null }, ct);
            return TypedResults.NoContent();
        }).WithName("RejectDocumentationPatch");

        mcp.MapGet("/tools", (IMediator m, CancellationToken ct) => m.Send(new GetMcpToolsQuery(), ct))
            .WithName("GetMcpTools");

        mcp.MapPost("/tools/run", (RunMcpToolCommand body, IMediator m, CancellationToken ct) => m.Send(body, ct))
            .WithName("RunMcpTool");

        mcp.MapGet("/learning-stats", ([FromQuery] int? projectId, IMediator m, CancellationToken ct) =>
                m.Send(new GetLearningStatsQuery { ProjectId = projectId }, ct))
            .WithName("GetLearningStats");

        return group;
    }
}

internal sealed record UpdateMcpSettingsBody(McpSettingsData Data);
internal sealed record UpdatePatternStatusBody(McpPatternStatus NewStatus);
