using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class DeleteSubscriptionHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<DeleteSubscriptionCommand>
{
    public async Task Handle(DeleteSubscriptionCommand request, CancellationToken cancellationToken)
    {
        await subscriptionService.DeleteSubscription(request.SubscriptionId, cancellationToken);
    }
}

public record DeleteSubscriptionCommand(int SubscriptionId) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Subscription(SubscriptionId), ProjectAction.Manage)
    ];
}
