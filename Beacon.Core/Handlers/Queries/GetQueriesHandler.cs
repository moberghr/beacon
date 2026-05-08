using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Queries;

internal sealed class GetQueriesHandler(IQueryService queryService)
    : IRequestHandler<GetQueriesQuery, PagedList<QueryData>>
{
    public async Task<PagedList<QueryData>> Handle(GetQueriesQuery request, CancellationToken cancellationToken)
    {
        return await queryService.GetQueries(request.Request, cancellationToken);
    }
}

public record GetQueriesQuery : IRequest<PagedList<QueryData>>
{
    public required GetQueriesRequest Request { get; init; }
}
