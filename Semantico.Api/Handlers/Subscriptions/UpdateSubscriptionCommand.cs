using MediatR;
using Microsoft.EntityFrameworkCore;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
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

        var Subscription = await _context.Subscriptions
            .Where(x => x.Id == request.SubscriptionId)
            .FirstAsync(cancellationToken);

        Subscription.Name = request.Name;
        Subscription.CronExpression = request.CronExpression;

        var result = await _context.SaveChangesAsync(cancellationToken);

        if (result > 0)
        {
            _recurringJobService.AddOrUpdate(Subscription.Id, Subscription.QueryId, Subscription.CronExpression);
        }

        return new();
    }
}

public class UpdateSubscriptionRequest : IRequest<UpdateSubscriptionResponse>
{
    public int SubscriptionId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string CronExpression { get; init; } = string.Empty;

}

public class UpdateSubscriptionResponse
{
}

