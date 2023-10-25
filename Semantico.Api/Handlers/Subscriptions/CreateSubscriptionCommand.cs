using MediatR;
using Microsoft.EntityFrameworkCore;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Data.Enums;
using Semantico.Api.Handlers.Queries;
using Semantico.Api.Validators;
using Semantico.Api.Worker.Services;

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

        var queryParams = await _context.QueryParameters
            .Where(x => x.QueryId == request.QueryId)
            .Select(x =>
                new QueryParameterResponseListData
                {
                    Description = x.Description,
                    Placeholder = x.Placeholder,
                    Name = x.Name,
                    Type = x.Type
                })
            .ToListAsync(cancellationToken);

        SubscriptionValidator.ValidateParameters(request.Parameters, queryParams);

        var subscription = new Subscription
        {
            Name = request.Name,
            CronExpression = request.CronExpression,
            QueryId = request.QueryId,
            Recipient = request.Recipient,
            NotificationType = request.NotificationType
        };

        _context.Subscriptions.Add(subscription);

        foreach (var subscriptionParameter in request.Parameters)
        {
            var parameter = new SubscriptionParameter
            {
                SubscriptionId = subscription.Id,
                QueryPlaceholder = subscriptionParameter.QueryPlaceholder,
                Value = subscriptionParameter.Value,
            };

            _context.SubscriptionParameters.Add(parameter);
        }

        await _context.SaveChangesAsync(cancellationToken);

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

    public List<SubscriptionParameterResponseListData> Parameters { get; init; } = new();
}

public class CreateSubscriptionResponse
{
}