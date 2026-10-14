using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class RemoveSubscriptionRecipientHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<RemoveSubscriptionRecipientCommand>
{
    public async Task Handle(RemoveSubscriptionRecipientCommand request, CancellationToken cancellationToken)
    {
        await subscriptionService.RemoveRecipient(request.SubscriptionId, request.RecipientId, cancellationToken);
    }
}

public record RemoveSubscriptionRecipientCommand(
    int SubscriptionId,
    [property: ProjectScopeExempt("recipients are project-less; detaching one needs Write on the subscription, checked")] int RecipientId) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Subscription(SubscriptionId), ProjectAction.Write)
    ];
}
