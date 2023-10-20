using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Data.Enums;
using Semantico.Api.Helpers;

namespace Semantico.Api.Handlers.Subscriptions;

public class GetSubscriptionsQuery : IRequestHandler<GetSubscriptionsRequest, GetSubscriptionsResponse>
{
    private readonly SemanticoContext _context;

    public GetSubscriptionsQuery(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<GetSubscriptionsResponse> Handle(GetSubscriptionsRequest request, CancellationToken cancellation)
    {
        var Subscriptions = await _context.Subscriptions
            .Include(x => x.Parameters)
            .WhereIf(request.SubscriptionId.HasValue, x => x.Id == request.SubscriptionId)
            .WhereIf(request.QueryId.HasValue, x => x.QueryId == request.QueryId)
            .WhereIf(request.NotificationType.HasValue, x => x.NotificationType == request.NotificationType)
            .Select(x =>
                new GetSubscriptionsResponseListData
                {
                    SubscriptionId = x.Id,
                    Name = x.Name,
                    QueryId = x.QueryId,
                    Recipient = x.Recipient,
                    NotificationType = x.NotificationType,
                    Parameters = x.Parameters
                })
            .ToListAsync(cancellation);

        return new GetSubscriptionsResponse
        {
            Subscriptions = Subscriptions
        };
    }
}

public class GetSubscriptionsRequest : IRequest<GetSubscriptionsResponse>
{
    public int? SubscriptionId { get; init; }

    public int? QueryId { get; init; }
    
    public NotificationType? NotificationType { get; init; }
}

public class GetSubscriptionsResponse
{
    public required List<GetSubscriptionsResponseListData> Subscriptions { get; init; }
}

public class GetSubscriptionsResponseListData
{
    public required int SubscriptionId { get; init; }

    public required string Name { get; init; }

    public required int QueryId { get; init; }

    public required NotificationType NotificationType { get; init; }

    public required string Recipient { get; init; }

    public required List<SubscriptionParameter> Parameters { get; init; }
}