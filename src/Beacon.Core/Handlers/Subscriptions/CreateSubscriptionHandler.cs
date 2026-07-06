using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class CreateSubscriptionHandler(ISubscriptionService subscriptionService)
    : IRequestHandler<CreateSubscriptionCommand, CreateSubscriptionResult>
{
    public async Task<CreateSubscriptionResult> Handle(CreateSubscriptionCommand request, CancellationToken cancellationToken)
    {
        var data = new SubscriptionData
        {
            QueryId = request.QueryId,
            CronExpression = request.CronExpression,
            MaxRows = request.MaxRows,
            TimeoutSeconds = request.TimeoutSeconds,
            IncludeAttachment = request.IncludeAttachment,
            ShowQuery = request.ShowQuery,
            StoreResults = request.StoreResults,
            CreateTasks = request.CreateTasks,
            Recipients = request.RecipientIds
                .Select(x =>
                    new RecipientData
                    {
                        RecipientId = x,
                        Name = string.Empty,
                        Destination = string.Empty,
                    })
                .ToList(),
        };

        var response = await subscriptionService.CreateSubscription(data, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message);
        }

        return new CreateSubscriptionResult(true, response.Message);
    }
}

public record CreateSubscriptionCommand(
    int QueryId,
    string CronExpression,
    List<int> RecipientIds,
    int? MaxRows,
    int? TimeoutSeconds,
    bool IncludeAttachment,
    bool ShowQuery,
    bool StoreResults,
    bool CreateTasks) : IRequest<CreateSubscriptionResult>;

public record CreateSubscriptionResult(bool Success, string? Message);
