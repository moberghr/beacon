using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.AiActors;

internal sealed class SetAiActorOwnerHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ILogger<SetAiActorOwnerHandler> logger) : IRequestHandler<SetAiActorOwnerCommand>
{
    public async Task Handle(SetAiActorOwnerCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (!actor.IsAdmin)
        {
            throw AccessRefusal.Of(logger, actor, "AI actor", request.ActorId, "Only an Admin can change an AI actor's creator.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ownerUserId = await AssignableUsers.ExternalIdOfActiveUserAsync(context, request.UserId, cancellationToken)
            ?? throw new InvalidOperationException("An AI actor can only be owned by an existing, enabled user.");

        var updated = await context.AiActors
            .Where(x => x.Id == request.ActorId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.CreatedByUserId, ownerUserId), cancellationToken);

        if (updated == 0)
        {
            throw new InvalidOperationException($"AI actor {request.ActorId} not found.");
        }
    }
}

/// <summary>
/// Makes a Beacon user (<c>UserId</c>, the user's id in the user directory) the creator of an AI actor, the user who may
/// then pause, resume, archive, refine and run it (for instance an actor created before creators were recorded). Admin
/// only; the user must be an existing, enabled user.
/// </summary>
public record SetAiActorOwnerCommand(int ActorId, int UserId) : IRequest;
