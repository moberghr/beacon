using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class AddSubscriptionRecipientsHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<AddSubscriptionRecipientsCommand>
{
    public async Task Handle(AddSubscriptionRecipientsCommand request, CancellationToken cancellationToken)
    {
        if (request.RecipientIds is null || request.RecipientIds.Count == 0)
        {
            throw new InvalidOperationException("At least one recipient id is required.");
        }

        await subscriptionService.AddRecipients(request.SubscriptionId, request.RecipientIds, cancellationToken);
    }
}

public record AddSubscriptionRecipientsCommand(int SubscriptionId, List<int> RecipientIds) : IRequest;
