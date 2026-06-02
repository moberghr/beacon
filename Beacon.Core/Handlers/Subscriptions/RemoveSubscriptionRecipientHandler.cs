using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class RemoveSubscriptionRecipientHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<RemoveSubscriptionRecipientCommand>
{
    public async Task Handle(RemoveSubscriptionRecipientCommand request, CancellationToken cancellationToken)
    {
        await subscriptionService.RemoveRecipient(request.SubscriptionId, request.RecipientId, cancellationToken);
    }
}

public record RemoveSubscriptionRecipientCommand(int SubscriptionId, int RecipientId) : IRequest;
