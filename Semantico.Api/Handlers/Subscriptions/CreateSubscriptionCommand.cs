using MediatR;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Data.Enums;
using Semantico.Api.Web;
using Semantico.Api.Worker.Services;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace Semantico.Api.Handlers.Subscriptions;

public class CreateSubscriptionCommand : IRequestHandler<CreateSubscriptionRequest, CreateSubscriptionResponse>
{
    private readonly SemanticoContext _context;
    private readonly IRecurringJobService _recurringJobService;

    public CreateSubscriptionCommand(SemanticoContext context, IRecurringJobService recurringJobService)
    {
        _context = context;
        _recurringJobService = recurringJobService;
    }

    public async Task<CreateSubscriptionResponse> Handle(CreateSubscriptionRequest request, CancellationToken cancellationToken)
    {
        CrontabSchedule.Parse(request.CronExpression);

        var query = await _context.Queries
            .Where(x => x.Id == request.QueryId)
            .FirstAsync();

        // Query does not have user-definable parameters, we will ignore them.
        if (query.Parameters.Count == 0)
        {
            request.Parameters.Clear();
        }
        else
        {
            if (request.Parameters.Count != query.Parameters.Count)
            {
                throw new Exception($"Defined subscription parameters count does not match specified query parameter count");
            }

            int matched = 0;
            foreach (var queryParam in query.Parameters)
            {
                foreach (var subscriptionParam in request.Parameters)
                {
                    if (subscriptionParam.Name == queryParam.Name)
                    {
                        // check if value can be casted to given type
                        ++matched;
                    }
                }
            }

            if (matched != query.Parameters.Count)
            {
                throw new Exception($"Not all requested query parameters are defined.");
            }
        }

        var subscription = new Subscription
        {
            Name = request.Name,
            CronExpression = request.CronExpression,
            QueryId = request.QueryId,
            Recipient = request.Recipient,
            NotificationType = request.NotificationType
        };

        _context.Subscriptions.Add(subscription);
        await _context.SaveChangesAsync(cancellationToken);

        foreach (var subscriptionParameter in request.Parameters)
        {
            var parameter = new SubscriptionParameter
            {
                SubscriptionId = subscription.Id,
                Name = subscriptionParameter.Name,
                Value = subscriptionParameter.Value,
            };

            _context.SubscriptionParameters.Add(parameter);
            await _context.SaveChangesAsync(cancellationToken);
        }

        _recurringJobService.AddOrUpdate(subscription.Id, request.CronExpression);

        return new();
    }
}

public class CreateSubscriptionRequest : IRequest<CreateSubscriptionResponse>
{
    public string Name { get; init; } = string.Empty;

    public string CronExpression { get; init; } = string.Empty;

    public string Recipient { get; init; } = string.Empty;
    
    public NotificationType NotificationType { get; init; }

    public int QueryId { get; init; }

    public List<SubscriptionParameter> Parameters { get; init; } = new();
}

public class CreateSubscriptionResponse
{
}