using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
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
            .WhereIf(request.SubscriptionId.HasValue, x => x.Id == request.SubscriptionId)
            .WhereIf(request.QueryId.HasValue, x => x.QueryId == request.QueryId)
            .Select(x =>
                new GetSubscriptionsResponseListData
                {
                    SubscriptionId = x.Id,
                    Name = x.Name,
                    QueryId = x.QueryId
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
}