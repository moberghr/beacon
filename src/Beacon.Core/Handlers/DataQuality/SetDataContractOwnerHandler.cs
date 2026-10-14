using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.DataQuality.SetDataContractOwner;

internal sealed class SetDataContractOwnerHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    ProjectRequestAuthorizer authorizer,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider,
    ILogger<SetDataContractOwnerHandler> logger) : IRequestHandler<SetDataContractOwnerCommand>
{
    public const string OwnerMayNotRunMessage =
        "The new owner cannot evaluate this data contract: they need access to run saved SQL in a project that holds its data source.";

    /// <summary>The data contract was changed (its owner, or archived) between the read of its owner and the change: nothing was written.</summary>
    public const string OwnerChangedMessage = "The data contract changed while its owner was being set (its owner was changed by someone else, or it was archived); nothing was changed. Reload it and try again.";

    public async Task Handle(SetDataContractOwnerCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        OwnerTransfer.Demand(
            await permissionAccessor.GetCurrentAsync(cancellationToken),
            actor,
            request.UserId,
            "data contract",
            request.DataContractId,
            "Only an Admin or a UserAdmin can change a data contract's owner.",
            logger);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ownerUserId = await AssignableUsers.ExternalIdOfActiveUserAsync(context, request.UserId, cancellationToken)
            ?? throw new InvalidOperationException("A data contract can only be owned by an existing, enabled user.");

        var current = await context.DataContracts
            .Where(x => x.Id == request.DataContractId)
            .Select(x =>
                new
                {
                    x.OwnerUserId
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Data contract {request.DataContractId} not found");
        if (current.OwnerUserId == ownerUserId)
        {
            return;
        }

        // Scheduled evaluations run as the owner: an owner who may not run the contract's rules would only have every
        // evaluation skipped.
        await authorizer.DemandOwnerMayAsync(
            ownerUserId,
            new ProjectCheck(ProjectResource.DataContract(request.DataContractId), ProjectAction.Run),
            OwnerMayNotRunMessage,
            cancellationToken);

        var modifiedTime = timeProvider.GetUtcNow().UtcDateTime;
        await securityAudit.SaveWithEventsAsync(
            context,
            () => context.DataContracts
                .Where(x => x.Id == request.DataContractId)
                .Where(x => x.OwnerUserId == current.OwnerUserId)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.OwnerUserId, ownerUserId)
                        .SetProperty(y => y.ModifiedByUserId, actor.UserId)
                        .SetProperty(y => y.ModifiedTime, modifiedTime),
                    cancellationToken),
            written => written == 0
                ? throw new BeaconConflictException(OwnerChangedMessage)
                :
                [
                    new SecurityAuditEntry(SecurityAuditActions.OwnerChanged)
                        .On(SecurityAuditResources.DataContract, request.DataContractId)
                        .With(new SecurityAuditDetails().Add("ownerUserId", request.UserId))
                        .Naming("previousOwnerUserId", current.OwnerUserId)
                ],
            cancellationToken);
    }
}

/// <summary>
/// Makes a Beacon user (<c>UserId</c>, the user's id in the user directory) the owner of a data contract, the user its
/// scheduled evaluations run as (for instance a contract created before owners were recorded). For an Admin or a
/// UserAdmin (who is not an Admin never names themselves: 403); the user must be an existing, enabled user who may run saved SQL on the contract's data source (409
/// otherwise); a contract that does not exist answers 404.
/// </summary>
[ProjectScopeExempt("ownership transfer, gated by the UserAdmin permission; the new owner's access is validated", Policy = BeaconPermissions.UserAdminPolicy)]
public record SetDataContractOwnerCommand(int DataContractId, int UserId) : IRequest;
