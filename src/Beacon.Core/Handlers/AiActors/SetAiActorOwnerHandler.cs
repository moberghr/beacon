using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.AiActors;

internal sealed class SetAiActorOwnerHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    ProjectRequestAuthorizer authorizer,
    ILogger<SetAiActorOwnerHandler> logger) : IRequestHandler<SetAiActorOwnerCommand>
{
    public const string OwnerMayNotQueryMessage =
        "The new creator cannot run this AI actor: they need access to work ad hoc in a project that holds its data source.";

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

        var exists = await context.AiActors
            .Where(x => x.Id == request.ActorId)
            .AnyAsync(cancellationToken);
        if (!exists)
        {
            throw new BeaconNotFoundException($"AI actor {request.ActorId} not found.");
        }

        // Background think cycles run as the creator: a creator who may not work ad hoc with the actor's data source
        // would only have every cycle skipped.
        await authorizer.DemandOwnerMayAsync(
            ownerUserId,
            new ProjectCheck(ProjectResource.AiActor(request.ActorId), ProjectAction.Query),
            OwnerMayNotQueryMessage,
            cancellationToken);

        await context.AiActors
            .Where(x => x.Id == request.ActorId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.CreatedByUserId, ownerUserId), cancellationToken);
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
