using MediatR;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
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

        var subscription = new Subscription
        {
            Name = request.Name,
            CronExpression = request.CronExpression,
            QueryId = request.QueryId,
        };

        _context.Subscriptions.Add(subscription);
        var result = await _context.SaveChangesAsync(cancellationToken);

        if (result > 0)
        {
            _recurringJobService.AddOrUpdate(subscription.Id, request.QueryId, request.CronExpression);
        }

        return new();
    }
}

public class CreateSubscriptionRequest : IRequest<CreateSubscriptionResponse>
{
    public string Name { get; init; } = string.Empty;

    public string CronExpression { get; init; } = string.Empty;

    public int QueryId { get; init; }
}

public class CreateSubscriptionResponse
{
}