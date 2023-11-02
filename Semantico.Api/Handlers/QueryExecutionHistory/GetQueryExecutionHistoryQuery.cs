using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Enums;
using Semantico.Api.Helpers;

namespace Semantico.Api.Handlers.Notifications;

public class GetQueryExecutionHistoryQuery : IRequestHandler<GetQueryExecutionHistoryRequest, GetQueryExecutionHistoryResponse>
{
    private readonly SemanticoContext _context;

    public GetQueryExecutionHistoryQuery(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<GetQueryExecutionHistoryResponse> Handle(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken)
    {
        var notifications = await _context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == request.SubscriptionId)
            .WhereIf(request.LastQueryExecutionHistoryId.HasValue, x => x.Id < request.LastQueryExecutionHistoryId)
            .WhereIf(request.NotificationSent.HasValue, x => x.NotificationSent == request.NotificationSent)
            .OrderByDescending(x => x.Id)
            .TakeIf(request.PageSize.HasValue, request.PageSize)
            .Select(x =>
                new GetNotificationsResponseDataList
                {
                    QueryExecutionHistoryId = x.Id,
                    Recipient = x.Recipient,
                    NotificationType = x.NotificationType,
                    ResultCount = x.ResultCount
                })
            .ToListAsync(cancellationToken);

        return new GetQueryExecutionHistoryResponse
        {
            LastQueryExecutionHistoryId = notifications.Last().QueryExecutionHistoryId,
            Notifications = notifications
        };
    }
}

public class GetQueryExecutionHistoryRequest : IRequest<GetQueryExecutionHistoryResponse>
{
    public required int SubscriptionId { get; init; }

    public int? PageSize { get; init; }

    public int? LastQueryExecutionHistoryId { get; init; }

    public bool? NotificationSent { get; init; }
}

public class GetQueryExecutionHistoryResponse
{
    public required List<GetNotificationsResponseDataList> Notifications { get; set; }

    public int? LastQueryExecutionHistoryId { get; init; }

}

public class GetNotificationsResponseDataList
{
    public required int QueryExecutionHistoryId { get; set; }

    public required string Recipient { get; set; }
    
    public required NotificationType NotificationType { get; set; }
    
    public required int ResultCount { get; set; }
}