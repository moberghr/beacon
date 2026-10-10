using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.DataQuality.SetDataContractOwner;

internal sealed class SetDataContractOwnerHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ILogger<SetDataContractOwnerHandler> logger) : IRequestHandler<SetDataContractOwnerCommand>
{
    public async Task Handle(SetDataContractOwnerCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (!actor.IsAdmin)
        {
            throw AccessRefusal.Of(logger, actor, "data contract", request.DataContractId, "Only an Admin can change a data contract's owner.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ownerUserId = await AssignableUsers.ExternalIdOfActiveUserAsync(context, request.UserId, cancellationToken)
            ?? throw new InvalidOperationException("A data contract can only be owned by an existing, enabled user.");

        var updated = await context.DataContracts
            .Where(x => x.Id == request.DataContractId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.OwnerUserId, ownerUserId), cancellationToken);

        if (updated == 0)
        {
            throw new BeaconException($"Data contract {request.DataContractId} not found");
        }
    }
}

/// <summary>
/// Makes a Beacon user (<c>UserId</c>, the user's id in the user directory) the owner of a data contract, for instance a
/// contract created before owners were recorded. Admin only; the user must be an existing, enabled user.
/// </summary>
public record SetDataContractOwnerCommand(int DataContractId, int UserId) : IRequest;
