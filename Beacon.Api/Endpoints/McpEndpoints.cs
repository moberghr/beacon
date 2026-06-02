using System.Security.Claims;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Mcp.RunMcpTool;
using Beacon.Core.Handlers.McpLearning;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
            HttpContext http,
            IDbContextFactory<BeaconContext> contextFactory,
            IMediator m,
            CancellationToken ct) =>
        {
            var reviewerId = await ResolveActorUserIdAsync(http, contextFactory, ct);
            await m.Send(new UpdatePatternStatusCommand
            {
                PatternId = id,
                NewStatus = body.NewStatus,
                ReviewedByUserId = reviewerId,
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

        mcp.MapPost("/documentation-patches/{id:int}/apply", async (
            int id,
            HttpContext http,
            IDbContextFactory<BeaconContext> contextFactory,
            IMediator m,
            CancellationToken ct) =>
        {
            var actorId = await ResolveActorUserIdAsync(http, contextFactory, ct);
            await m.Send(new ApplyDocumentationPatchCommand { PatchId = id, AppliedByUserId = actorId }, ct);
            return TypedResults.NoContent();
        }).WithName("ApplyDocumentationPatch");

        mcp.MapPost("/documentation-patches/{id:int}/reject", async (
            int id,
            HttpContext http,
            IDbContextFactory<BeaconContext> contextFactory,
            IMediator m,
            CancellationToken ct) =>
        {
            var actorId = await ResolveActorUserIdAsync(http, contextFactory, ct);
            await m.Send(new RejectDocumentationPatchCommand { PatchId = id, RejectedByUserId = actorId }, ct);
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

    /// <summary>
    /// Resolves the calling user's <c>BeaconUser.Id</c> (int) from the authenticated
    /// principal's <c>ClaimTypes.NameIdentifier</c> (string ExternalId). Returns null if
    /// the claim is absent or no matching user row exists. Mutating MCP endpoints feed
    /// this value into the audit columns (§1.7 / §9.5) so the audit trail is never null.
    /// </summary>
    private static async Task<int?> ResolveActorUserIdAsync(
        HttpContext context,
        IDbContextFactory<BeaconContext> contextFactory,
        CancellationToken cancellationToken)
    {
        var externalId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(externalId))
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users
            .Where(x => x.ExternalId == externalId)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

internal sealed record UpdateMcpSettingsBody(McpSettingsData Data);
internal sealed record UpdatePatternStatusBody(McpPatternStatus NewStatus);
