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
}

public class CreateSubscriptionResponse
{
}