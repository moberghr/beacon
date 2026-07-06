using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class GetSubscriptionsHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<GetSubscriptionsQuery, GetSubscriptionsResult>
{
    public async Task<GetSubscriptionsResult> Handle(GetSubscriptionsQuery request, CancellationToken cancellationToken)
    {
        var data = await subscriptionService.GetSubscriptions(
            null,
            null,
            null,
            request.Search,
            cancellationToken);

        var entries = data
            .Select(x =>
                new SubscriptionEntry(
                    x.SubscriptionId ?? 0,
                    x.QueryId,
                    x.QueryName,
                    x.CronExpression,
                    x.Recipients.Count,
                    x.Recipients.Select(y => y.Name).ToList(),
                    x.AiActorId,
                    x.AiActorName,
                    x.CreateTasks,
                    x.StoreResults))
            .ToList();

        return new GetSubscriptionsResult(entries);
    }
}

public record GetSubscriptionsQuery(string? Search = null) : IRequest<GetSubscriptionsResult>;

public record GetSubscriptionsResult(List<SubscriptionEntry> Entries);

public record SubscriptionEntry(
    int Id,
    int QueryId,
    string QueryName,
    string CronExpression,
    int RecipientCount,
    List<string> RecipientNames,
    int? AiActorId,
    string? AiActorName,
    bool CreateTasks,
    bool StoreResults);
