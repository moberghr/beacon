using Beacon.Api.Authentication;
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

        // The settings (limits, PII detection and its custom patterns, prompts): an Auditor or a SecurityAdmin reads them, a
        // SecurityAdmin changes them.
        mcp.MapGet("/settings", (IMediator m, CancellationToken ct) => m.Send(new GetMcpSettingsQuery(), ct))
            .WithName("GetMcpSettings")
            .RequireBeaconPermission(BeaconPermissions.AuditorOrSecurityAdminPolicy);

        // The settings the deployment pins (Beacon:Mcp locks): the page disables them, a write that changes one is refused (409).
        mcp.MapGet("/settings/locks", (IMediator m, CancellationToken ct) => m.Send(new GetMcpDeploymentLocksQuery(), ct))
            .WithName("GetMcpDeploymentLocks")
            .RequireBeaconPermission(BeaconPermissions.AuditorOrSecurityAdminPolicy);

        mcp.MapPut("/settings", async (UpdateMcpSettingsBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateMcpSettingsCommand(body.Data), ct);
            return TypedResults.NoContent();
        })
        .WithName("UpdateMcpSettings")
        .RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        // Per-project overrides. GET mirrors the global GET, PUT likewise.
        mcp.MapGet("/projects/{projectId:int}/settings", (int projectId, IMediator m, CancellationToken ct) =>
                m.Send(new GetMcpProjectSettingsQuery(projectId), ct))
            .WithName("GetMcpProjectSettings")
            .RequireBeaconPermission(BeaconPermissions.AuditorOrSecurityAdminPolicy);

        mcp.MapPut("/projects/{projectId:int}/settings", async (int projectId, UpdateMcpProjectSettingsBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateMcpProjectSettingsCommand(projectId, body.Data), ct);
            return TypedResults.NoContent();
        })
        .WithName("UpdateMcpProjectSettings")
        .RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        mcp.MapGet("/learned-patterns", ([AsParameters] GetLearnedPatternsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetLearnedPatterns");

        // Approve, reject or set back to pending (anything else: 400); needs the project admin role in the pattern's project.
        // Decides the content the reviewer saw (its contentHash): 409 when the pattern changed since.
        mcp.MapPut("/learned-patterns/{id:int}/status", async (
            int id,
            UpdatePatternStatusBody body,
            IMediator m,
            CancellationToken ct) =>
        {
            await m.Send(new UpdatePatternStatusCommand
            {
                PatternId = id,
                NewStatus = body.NewStatus,
                ReviewedContentHash = body.ReviewedContentHash
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

        // The Auditor's paged audit export. Rows are returned exactly as stored (retention redaction already
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
            .RequireBeaconPermission(BeaconPermissions.AuditorPolicy);

        return group;
    }
}

internal sealed record UpdateMcpSettingsBody(McpSettingsData Data);
internal sealed record UpdateMcpProjectSettingsBody(McpProjectSettingsData Data);
/// <param name="NewStatus">Approved, Rejected or Pending.</param>
/// <param name="ReviewedContentHash">The pattern's <c>contentHash</c> from the learned-pattern list, as reviewed.</param>
internal sealed record UpdatePatternStatusBody(McpPatternStatus NewStatus, string ReviewedContentHash);
