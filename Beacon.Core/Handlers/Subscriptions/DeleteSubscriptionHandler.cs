using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class DeleteSubscriptionHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<DeleteSubscriptionCommand>
{
    public async Task Handle(DeleteSubscriptionCommand request, CancellationToken cancellationToken)
    {
        await subscriptionService.DeleteSubscription(request.SubscriptionId, cancellationToken);
    }
}

public record DeleteSubscriptionCommand(int SubscriptionId) : IRequest;
