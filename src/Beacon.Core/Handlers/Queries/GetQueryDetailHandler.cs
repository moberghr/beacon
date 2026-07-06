using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Queries;

internal sealed class GetQueryDetailHandler(IQueryService queryService)
    : IRequestHandler<GetQueryDetailQuery, QueryDetailsData>
{
    public async Task<QueryDetailsData> Handle(GetQueryDetailQuery request, CancellationToken cancellationToken)
    {
        var details = await queryService.GetQueryDetails(request.QueryId, cancellationToken);

        if (details == null)
        {
            throw new InvalidOperationException($"Query #{request.QueryId} not found.");
        }

        return details;
    }
}

public record GetQueryDetailQuery : IRequest<QueryDetailsData>
{
    public required int QueryId { get; init; }
}
