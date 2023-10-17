using MediatR;
using Microsoft.EntityFrameworkCore;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Data.Enums;
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
            .Where(x => x.Id == request.SubscriptionId)
            .FirstAsync(cancellationToken);

        var shouldUpdateHangfire = subscription.CronExpression != request.CronExpression;

        subscription.Name = request.Name;
        subscription.CronExpression = request.CronExpression;
        subscription.Recipient = request.Recipient;
        subscription.NotificationType = request.NotificationType;

        await _context.SaveChangesAsync(cancellationToken);

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
}

public class UpdateSubscriptionResponse
{
}

