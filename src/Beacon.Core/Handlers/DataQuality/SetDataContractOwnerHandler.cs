using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.DataQuality.SetDataContractOwner;

internal sealed class SetDataContractOwnerHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ProjectRequestAuthorizer authorizer,
    ILogger<SetDataContractOwnerHandler> logger) : IRequestHandler<SetDataContractOwnerCommand>
{
    public const string OwnerMayNotRunMessage =
        "The new owner cannot evaluate this data contract: they need access to run saved SQL in a project that holds its data source.";

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

        var exists = await context.DataContracts
            .Where(x => x.Id == request.DataContractId)
            .AnyAsync(cancellationToken);
        if (!exists)
        {
            throw new BeaconNotFoundException($"Data contract {request.DataContractId} not found");
        }

        // Scheduled evaluations run as the owner: an owner who may not run the contract's rules would only have every
        // evaluation skipped.
        await authorizer.DemandOwnerMayAsync(
            ownerUserId,
            new ProjectCheck(ProjectResource.DataContract(request.DataContractId), ProjectAction.Run),
            OwnerMayNotRunMessage,
            cancellationToken);

        await context.DataContracts
            .Where(x => x.Id == request.DataContractId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.OwnerUserId, ownerUserId), cancellationToken);
    }
}

/// <summary>
/// Makes a Beacon user (<c>UserId</c>, the user's id in the user directory) the owner of a data contract, the user its
/// scheduled evaluations run as (for instance a contract created before owners were recorded). Admin only; the user must
/// be an existing, enabled user who may run saved SQL on the contract's data source (409 otherwise); a contract that does
/// not exist answers 404.
/// </summary>
[ProjectScopeExempt("admin-gated ownership transfer; the new owner's access is validated", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record SetDataContractOwnerCommand(int DataContractId, int UserId) : IRequest;
