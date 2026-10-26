using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class GetAiActorPlanHandler(
    AiActorService aiActorService,
    IOptionsMonitor<AiActorOptions> options,
    TimeProvider timeProvider,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    IProjectAccessEvaluator projectAccess,
    IDbContextFactory<BeaconContext> contextFactory,
    BeaconConfiguration configuration)
    : IRequestHandler<GetAiActorPlanQuery, GetAiActorPlanResult?>
{
    private const string WriteRefusal = "Deciding on this plan takes write access to the AI actor's project.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<GetAiActorPlanResult?> Handle(
        GetAiActorPlanQuery request,
        CancellationToken cancellationToken)
    {
        var plan = await aiActorService.GetPlanAsync(request.PlanId, cancellationToken);

        if (plan == null)
            return null;

        var proposedActions = !string.IsNullOrEmpty(plan.ActionsJson)
            ? JsonSerializer.Deserialize<List<ProposedAction>>(plan.ActionsJson, JsonOptions) ?? []
            : [];

        var findings = !string.IsNullOrEmpty(plan.FindingsJson)
            ? JsonSerializer.Deserialize<List<string>>(plan.FindingsJson, JsonOptions) ?? []
            : [];

        var expiresAt = options.CurrentValue.PlanExpiresAt(plan.ProposedAt);
        var isExpired = plan.Status == AiActorPlanStatus.PendingApproval && timeProvider.GetUtcNow().UtcDateTime >= expiresAt;
        var decision = plan.Status == AiActorPlanStatus.PendingApproval && !isExpired
            ? await DecisionOfAsync(plan.Id, plan.AiActorId, plan.AiActor.CreatedByUserId, cancellationToken)
            : (MayApprove: false, ApproveBlockedReason: (string?)null, MayRejectOrRevise: false);

        return new GetAiActorPlanResult
        {
            PlanId = plan.Id,
            ActorId = plan.AiActorId,
            ActorName = plan.AiActor.Name,
            Status = plan.Status,
            UserInstruction = plan.UserInstruction,
            Analysis = plan.Analysis,
            Findings = findings,
            ProposedActions = proposedActions,
            ProposedAt = plan.ProposedAt,
            ExpiresAt = expiresAt,
            IsExpired = isExpired,
            ReviewedAt = plan.ReviewedAt,
            ReviewedByUserId = plan.ReviewedByUserId,
            ReviewerComment = plan.ReviewerComment,
            ExecutedAt = plan.ExecutedAt,
            ExecutionId = plan.AiActorExecutionId,
            TokensUsed = plan.TokensUsed,
            EstimatedCost = plan.EstimatedCost,
            Model = plan.Model,
            Version = plan.Version,
            ParentPlanId = plan.ParentPlanId,
            MayApprove = decision.MayApprove,
            ApproveBlockedReason = decision.ApproveBlockedReason,
            MayRejectOrRevise = decision.MayRejectOrRevise
        };
    }

    // What the caller may decide on a pending plan, by the rules its approve, reject and revise handlers enforce: deciding
    // writes through the actor's data source, so it takes Write on it first.
    private async Task<(bool MayApprove, string? ApproveBlockedReason, bool MayRejectOrRevise)> DecisionOfAsync(
        int planId,
        int actorId,
        string? creatorUserId,
        CancellationToken cancellationToken)
    {
        var writable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Write);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var mayWrite = await context.AiActors
            .Where(x => x.Id == actorId)
            .WhereSourceAccessible(context, writable, x => x.DataSourceId)
            .AnyAsync(cancellationToken);

        if (!mayWrite)
        {
            return (false, WriteRefusal, false);
        }

        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        var blocked = await AiActorOwnership.PlanApproveBlockedReasonAsync(caller, permissions, context, configuration, planId, creatorUserId, cancellationToken);

        return (blocked == null, blocked, caller.IsOwnerOrAdmin(creatorUserId));
    }
}

