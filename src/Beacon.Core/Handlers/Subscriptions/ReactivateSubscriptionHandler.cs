using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class ReactivateSubscriptionHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<ReactivateSubscriptionCommand>
{
    public async Task Handle(ReactivateSubscriptionCommand request, CancellationToken cancellationToken)
    {
        await subscriptionService.ReactivateSubscription(request.SubscriptionId, cancellationToken);
    }
}

public record ReactivateSubscriptionCommand(int SubscriptionId) : IRequest;
