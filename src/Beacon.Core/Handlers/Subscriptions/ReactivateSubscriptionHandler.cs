using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class ReactivateSubscriptionHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<ReactivateSubscriptionCommand>
{
    public async Task Handle(ReactivateSubscriptionCommand request, CancellationToken cancellationToken)
    {
        await subscriptionService.ReactivateSubscription(request.SubscriptionId, cancellationToken);
    }
}

public record ReactivateSubscriptionCommand(int SubscriptionId) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Subscription(SubscriptionId), ProjectAction.Write)
    ];
}
