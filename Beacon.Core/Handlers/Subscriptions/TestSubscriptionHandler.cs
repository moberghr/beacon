using Beacon.Core.Worker;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class TestSubscriptionHandler(IJobService jobService)
    : IRequestHandler<TestSubscriptionCommand>
{
    public async Task Handle(TestSubscriptionCommand request, CancellationToken cancellationToken)
    {
        await jobService.ExecuteQuery(request.SubscriptionId);
    }
}

public record TestSubscriptionCommand(int SubscriptionId) : IRequest;
