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

public class UpdateSubscriptionCommand : IRequestHandler<UpdateSubscriptionRequest, UpdateSubscriptionResponse>
{
    private readonly SemanticoContext _context;
    private readonly IRecurringJobService _recurringJobService;

    public UpdateSubscriptionCommand(SemanticoContext context, IRecurringJobService recurringJobService)
    {
        _context = context;
        _recurringJobService = recurringJobService;
    }

    public async Task<UpdateSubscriptionResponse> Handle(UpdateSubscriptionRequest request, CancellationToken cancellationToken)
    {
        CrontabSchedule.Parse(request.CronExpression);

        var subscription = await _context.Subscriptions
            .Include(subscription => subscription.Parameters)
            .Where(x => x.Id == request.SubscriptionId)
            .SingleAsync(cancellationToken);

        var queryParams = await _context.QueryParameters
            .Where(x => x.QueryId == subscription.QueryId)
            .Select(x =>
                new QueryParameterResponseListData
                {
                    Type = x.Type,
                    Name = x.Name,
                    Description = x.Description,
                    Placeholder = x.Placeholder,
                })
            .ToListAsync(cancellationToken);

        SubscriptionValidator.ValidateParameters(request.Parameters, queryParams);
        
        var shouldUpdateHangfire = subscription.CronExpression != request.CronExpression;

        subscription.Name = request.Name;
        subscription.CronExpression = request.CronExpression;
        subscription.Recipient = request.Recipient;
        subscription.NotificationType = request.NotificationType;

        foreach (var subscriptionParameter in subscription.Parameters)
        {
            subscriptionParameter.Archive();
        }

        foreach (var subscriptionParameter in request.Parameters)
        {
            var subscriptionParam = new SubscriptionParameter
            {
                SubscriptionId = subscription.Id,
                QueryPlaceholder = subscriptionParameter.QueryPlaceholder,
                Value = subscriptionParameter.Value
            };

            _context.SubscriptionParameters.Add(subscriptionParam);
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (shouldUpdateHangfire)
        {
            _recurringJobService.AddOrUpdate(subscription.Id, subscription.Name, subscription.CronExpression);
        }

        return new();
    }
}

public class UpdateSubscriptionRequest : IRequest<UpdateSubscriptionResponse>
{
    public int SubscriptionId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CronExpression { get; init; } = string.Empty;

    public NotificationType NotificationType { get; init; }
    
    public string Recipient { get; init; } = string.Empty;

    public List<SubscriptionParameterResponseListData> Parameters { get; init; } = new();
}

public class UpdateSubscriptionResponse
{
}

