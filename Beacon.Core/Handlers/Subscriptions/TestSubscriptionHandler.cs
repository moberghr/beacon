using Beacon.Core.Worker;
using Hangfire;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class TestSubscriptionHandler(IJobService jobService)
    : IRequestHandler<TestSubscriptionCommand>
{
    public async Task Handle(TestSubscriptionCommand request, CancellationToken cancellationToken)
    {
        // Synchronous handler invocation outside of Hangfire — pass the
        // null sentinel; ExecuteQuery falls back to CancellationToken.None.
        await jobService.ExecuteQuery(request.SubscriptionId, JobCancellationToken.Null);
    }
}

public record TestSubscriptionCommand(int SubscriptionId) : IRequest;
