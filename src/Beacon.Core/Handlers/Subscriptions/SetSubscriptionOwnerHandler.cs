using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class SetSubscriptionOwnerHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    ProjectRequestAuthorizer authorizer,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider,
    ILogger<SetSubscriptionOwnerHandler> logger) : IRequestHandler<SetSubscriptionOwnerCommand>
{
    public const string OwnerMayNotRunMessage =
        "The new owner cannot run this subscription's query: they need access to run saved SQL in a project that holds all of its data sources.";

    /// <summary>The subscription was changed (its owner, or archived) between the read of its owner and the change: nothing was written.</summary>
    public const string OwnerChangedMessage = "The subscription changed while its owner was being set (its owner was changed by someone else, or it was archived); nothing was changed. Reload it and try again.";

    public async Task Handle(SetSubscriptionOwnerCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        OwnerTransfer.Demand(
            await permissionAccessor.GetCurrentAsync(cancellationToken),
            actor,
            request.UserId,
            "subscription",
            request.SubscriptionId,
            "Only an Admin or a UserAdmin can change a subscription's owner.",
            logger);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ownerUserId = await AssignableUsers.ExternalIdOfActiveUserAsync(context, request.UserId, cancellationToken)
            ?? throw new InvalidOperationException("A subscription can only be owned by an existing, enabled user.");

        var current = await context.Subscriptions
            .Where(x => x.Id == request.SubscriptionId)
            .Select(x =>
                new
                {
                    x.OwnerUserId
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Subscription {request.SubscriptionId} not found.");
        if (current.OwnerUserId == ownerUserId)
        {
            return;
        }

        // Scheduled runs run as the owner: an owner who may not run the query would only have every run skipped.
        await authorizer.DemandOwnerMayAsync(
            ownerUserId,
            new ProjectCheck(ProjectResource.Subscription(request.SubscriptionId), ProjectAction.Run),
            OwnerMayNotRunMessage,
            cancellationToken);

        var modifiedTime = timeProvider.GetUtcNow().UtcDateTime;
        await securityAudit.SaveWithEventsAsync(
            context,
            () => context.Subscriptions
                .Where(x => x.Id == request.SubscriptionId)
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
                        .On(SecurityAuditResources.Subscription, request.SubscriptionId)
                        .With(new SecurityAuditDetails().Add("ownerUserId", request.UserId))
                        .Naming("previousOwnerUserId", current.OwnerUserId)
                ],
            cancellationToken);
    }
}

/// <summary>
/// Makes a Beacon user (<c>UserId</c>, the user's id in the user directory) the owner of a subscription, the user its
/// scheduled runs run as (for instance a subscription created before owners were recorded, or one whose owner left).
/// For an Admin or a UserAdmin (who is not an Admin never names themselves: 403); the user must be an existing, enabled user who may run the subscription's query (409
/// otherwise); a subscription that does not exist answers 404.
/// </summary>
[ProjectScopeExempt("ownership transfer, gated by the UserAdmin permission; the new owner's access is validated", Policy = BeaconPermissions.UserAdminPolicy)]
public record SetSubscriptionOwnerCommand(int SubscriptionId, int UserId) : IRequest;
