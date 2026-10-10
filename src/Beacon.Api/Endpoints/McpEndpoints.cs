using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Mcp.RunMcpTool;
using Beacon.Core.Handlers.McpAudit;
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

        // The settings (limits, PII detection and its custom patterns, prompts) are an Admin's to read and to change.
        mcp.MapGet("/settings", (IMediator m, CancellationToken ct) => m.Send(new GetMcpSettingsQuery(), ct))
            .WithName("GetMcpSettings")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        mcp.MapPut("/settings", async (UpdateMcpSettingsBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateMcpSettingsCommand(body.Data), ct);
            return TypedResults.NoContent();
        })
        .WithName("UpdateMcpSettings")
        .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        // Per-project overrides (spec mcp-project-settings). GET mirrors the global GET, PUT likewise; both are admin.
        mcp.MapGet("/projects/{projectId:int}/settings", (int projectId, IMediator m, CancellationToken ct) =>
                m.Send(new GetMcpProjectSettingsQuery(projectId), ct))
            .WithName("GetMcpProjectSettings")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        mcp.MapPut("/projects/{projectId:int}/settings", async (int projectId, UpdateMcpProjectSettingsBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateMcpProjectSettingsCommand(projectId, body.Data), ct);
            return TypedResults.NoContent();
        })
        .WithName("UpdateMcpProjectSettings")
        .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        mcp.MapGet("/learned-patterns", ([AsParameters] GetLearnedPatternsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetLearnedPatterns");

        mcp.MapPut("/learned-patterns/{id:int}/status", async (
            int id,
            UpdatePatternStatusBody body,
            IActorUserResolver actorResolver,
            IMediator m,
            CancellationToken ct) =>
        {
            var reviewerId = await actorResolver.ResolveActorUserIdAsync(ct);
            await m.Send(new UpdatePatternStatusCommand
            {
                PatternId = id,
                NewStatus = body.NewStatus,
                ReviewedByUserId = reviewerId,
            }, ct);
            return TypedResults.NoContent();
        }).WithName("UpdatePatternStatus");

        mcp.MapGet("/documentation-patches", ([AsParameters] GetDocumentationPatchesQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetDocumentationPatches");

        mcp.MapPost("/documentation-patches/{id:int}/apply", async (
            int id,
            IActorUserResolver actorResolver,
            IMediator m,
            CancellationToken ct) =>
        {
            var actorId = await actorResolver.ResolveActorUserIdAsync(ct);
            await m.Send(new ApplyDocumentationPatchCommand { PatchId = id, AppliedByUserId = actorId }, ct);
            return TypedResults.NoContent();
        }).WithName("ApplyDocumentationPatch");

        mcp.MapPost("/documentation-patches/{id:int}/reject", async (
            int id,
            IActorUserResolver actorResolver,
            IMediator m,
            CancellationToken ct) =>
        {
            var actorId = await actorResolver.ResolveActorUserIdAsync(ct);
            await m.Send(new RejectDocumentationPatchCommand { PatchId = id, RejectedByUserId = actorId }, ct);
            return TypedResults.NoContent();
        }).WithName("RejectDocumentationPatch");

        mcp.MapGet("/tools", (IMediator m, CancellationToken ct) => m.Send(new GetMcpToolsQuery(), ct))
            .WithName("GetMcpTools");

        // §1.4 — the playground dispatches ask/query, which execute SQL; same Execute scope as
        // the MCP route and the REST SQL endpoints, so a Read-scoped key can't run SQL through it.
        mcp.MapPost("/tools/run", (RunMcpToolCommand body, IMediator m, CancellationToken ct) => m.Send(body, ct))
            .WithName("RunMcpTool")
            .RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        mcp.MapGet("/learning-stats", ([FromQuery] int? projectId, IMediator m, CancellationToken ct) =>
                m.Send(new GetLearningStatsQuery { ProjectId = projectId }, ct))
            .WithName("GetLearningStats");

        // Admin, paged audit export. Rows are returned exactly as stored (retention redaction already
        // applied); reading the audit is itself audited (Beacon.Audit 9103, §9.5).
        mcp.MapGet("/audit", async (
                [FromQuery] DateTime from,
                [FromQuery] DateTime to,
                [FromQuery] int? projectId,
                [FromQuery] string? tool,
                [FromQuery] string? callerHash,
                [FromQuery] int? userId,
                IActorUserResolver actorResolver,
                IMediator m,
                CancellationToken ct,
                [FromQuery] int page = 1,
                [FromQuery] int pageSize = 100) =>
            {
                var actorId = await actorResolver.ResolveActorUserIdAsync(ct);

                return await m.Send(
                    new GetMcpAuditLogsQuery(from, to, projectId, tool, callerHash, userId, page, pageSize, actorId), ct);
            })
            .WithName("GetMcpAuditLogs")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        return group;
    }
}

internal sealed record UpdateMcpSettingsBody(McpSettingsData Data);
internal sealed record UpdateMcpProjectSettingsBody(McpProjectSettingsData Data);
internal sealed record UpdatePatternStatusBody(McpPatternStatus NewStatus);
