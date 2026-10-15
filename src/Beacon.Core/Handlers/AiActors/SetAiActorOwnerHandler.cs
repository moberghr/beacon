using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.AiActors;

internal sealed class SetAiActorOwnerHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    ProjectRequestAuthorizer authorizer,
    ISecurityAuditRecorder securityAudit,
    ILogger<SetAiActorOwnerHandler> logger) : IRequestHandler<SetAiActorOwnerCommand>
{
    public const string OwnerMayNotQueryMessage =
        "The new creator cannot run this AI actor: they need access to work ad hoc in a project that holds its data source.";

    /// <summary>The AI actor was changed (its owner, or archived) between the read of its owner and the change: nothing was written.</summary>
    public const string OwnerChangedMessage = "The AI actor changed while its owner was being set (its owner was changed by someone else, or it was archived); nothing was changed. Reload it and try again.";

    public async Task Handle(SetAiActorOwnerCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        OwnerTransfer.Demand(
            await permissionAccessor.GetCurrentAsync(cancellationToken),
            actor,
            request.UserId,
            "AI actor",
            request.ActorId,
            "Only an Admin or a UserAdmin can change an AI actor's creator.",
            logger);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ownerUserId = await AssignableUsers.ExternalIdOfActiveUserAsync(context, request.UserId, cancellationToken)
            ?? throw new InvalidOperationException("An AI actor can only be owned by an existing, enabled user.");

        var current = await context.AiActors
            .Where(x => x.Id == request.ActorId)
            .Select(x =>
                new
                {
                    x.CreatedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"AI actor {request.ActorId} not found.");
        if (current.CreatedByUserId == ownerUserId)
        {
            return;
        }

        // Background think cycles run as the creator: a creator who may not work ad hoc with the actor's data source
        // would only have every cycle skipped.
        await authorizer.DemandOwnerMayAsync(
            ownerUserId,
            new ProjectCheck(ProjectResource.AiActor(request.ActorId), ProjectAction.Query),
            OwnerMayNotQueryMessage,
            cancellationToken);

        await securityAudit.SaveWithEventsAsync(
            context,
            () => context.AiActors
                .Where(x => x.Id == request.ActorId)
                .Where(x => x.CreatedByUserId == current.CreatedByUserId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.CreatedByUserId, ownerUserId), cancellationToken),
            written => written == 0
                ? throw new BeaconConflictException(OwnerChangedMessage)
                :
                [
                    new SecurityAuditEntry(SecurityAuditActions.OwnerChanged)
                        .On(SecurityAuditResources.AiActor, request.ActorId)
                        .With(new SecurityAuditDetails().Add("ownerUserId", request.UserId))
                        .Naming("previousOwnerUserId", current.CreatedByUserId)
                ],
            cancellationToken);
    }
}

/// <summary>
/// Makes a Beacon user (<c>UserId</c>, the user's id in the user directory) the creator of an AI actor, the user who may
/// then pause, resume, archive, refine and run it and whom its background think cycles run as (for instance an actor
/// created before creators were recorded). For an Admin or a UserAdmin (who is not an Admin never names themselves: 403); the user must be an existing, enabled user who
/// may work ad hoc with the actor's data source (409 otherwise); an AI actor that does not exist answers 404.
/// </summary>
[ProjectScopeExempt("ownership transfer, gated by the UserAdmin permission; the new owner's access is validated", Policy = BeaconPermissions.UserAdminPolicy)]
public record SetAiActorOwnerCommand(int ActorId, int UserId) : IRequest;
