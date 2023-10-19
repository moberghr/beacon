using MediatR;
using Microsoft.EntityFrameworkCore;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Data.Enums;
using Semantico.Api.Validators;
using Semantico.Api.Worker.Services;
using System.Reflection.Metadata;

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
            .Include(subscription => subscription.Query)
            .ThenInclude(query => query.Parameters)
            .Where(x => x.Id == request.SubscriptionId)
            .SingleAsync(cancellationToken);

        SubscriptionValidator.ValidateParameters(request.Parameters, subscription.Query.Parameters);
        
        var shouldUpdateHangfire = subscription.CronExpression != request.CronExpression;

        subscription.Name = request.Name;
        subscription.CronExpression = request.CronExpression;
        subscription.Recipient = request.Recipient;
        subscription.NotificationType = request.NotificationType;

        await _context.SaveChangesAsync(cancellationToken);

        foreach (var subscriptionRequestParameter in request.Parameters)
        {
            var subscriptionParameter = subscription.Parameters
                .Where(x => x.QueryPlaceholder == subscriptionRequestParameter.QueryPlaceholder)
                .SingleOrDefault();

            if (subscriptionParameter == null)
            {
                subscriptionParameter = new SubscriptionParameter
                {
                    SubscriptionId = subscription.Id,
                    QueryPlaceholder = subscriptionRequestParameter.QueryPlaceholder,
                    Value = subscriptionRequestParameter.Value
                };

                _context.SubscriptionParameters.Add(subscriptionParameter);
            }
            else
            {
                subscriptionParameter.Value = subscriptionRequestParameter.Value;
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        if (shouldUpdateHangfire)
        {
            _recurringJobService.AddOrUpdate(subscription.Id, subscription.CronExpression);
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

    public List<SubscriptionParameter> Parameters { get; init; } = new();
}

public class UpdateSubscriptionResponse
{
}

