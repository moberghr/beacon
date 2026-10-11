using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class SetSubscriptionOwnerHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ProjectRequestAuthorizer authorizer,
    ILogger<SetSubscriptionOwnerHandler> logger) : IRequestHandler<SetSubscriptionOwnerCommand>
{
    public const string OwnerMayNotRunMessage =
        "The new owner cannot run this subscription's query: they need access to run saved SQL in a project that holds all of its data sources.";

    public async Task Handle(SetSubscriptionOwnerCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (!actor.IsAdmin)
        {
            throw AccessRefusal.Of(logger, actor, "subscription", request.SubscriptionId, "Only an Admin can change a subscription's owner.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ownerUserId = await AssignableUsers.ExternalIdOfActiveUserAsync(context, request.UserId, cancellationToken)
            ?? throw new InvalidOperationException("A subscription can only be owned by an existing, enabled user.");

        var exists = await context.Subscriptions
            .Where(x => x.Id == request.SubscriptionId)
            .AnyAsync(cancellationToken);
        if (!exists)
        {
            throw new BeaconNotFoundException($"Subscription {request.SubscriptionId} not found.");
        }

        // Scheduled runs run as the owner: an owner who may not run the query would only have every run skipped.
        await authorizer.DemandOwnerMayAsync(
            ownerUserId,
            new ProjectCheck(ProjectResource.Subscription(request.SubscriptionId), ProjectAction.Run),
            OwnerMayNotRunMessage,
            cancellationToken);

        await context.Subscriptions
            .Where(x => x.Id == request.SubscriptionId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.OwnerUserId, ownerUserId), cancellationToken);
    }
}

/// <summary>
/// Makes a Beacon user (<c>UserId</c>, the user's id in the user directory) the owner of a subscription, the user its
/// scheduled runs run as (for instance a subscription created before owners were recorded, or one whose owner left).
/// Admin only; the user must be an existing, enabled user who may run the subscription's query (409 otherwise); a
/// subscription that does not exist answers 404.
/// </summary>
[ProjectScopeExempt("admin-gated ownership transfer; the new owner's access is validated", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record SetSubscriptionOwnerCommand(int SubscriptionId, int UserId) : IRequest;
