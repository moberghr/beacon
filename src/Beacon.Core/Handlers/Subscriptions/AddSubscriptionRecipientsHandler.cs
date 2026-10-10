using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

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

public record AddSubscriptionRecipientsCommand(
    int SubscriptionId,
    [property: ProjectScopeExempt("recipients are project-less; attaching them needs Write on the subscription, checked")] List<int> RecipientIds) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Subscription(SubscriptionId), ProjectAction.Write)
    ];
}
