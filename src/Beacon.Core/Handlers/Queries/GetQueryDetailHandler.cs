using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

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

public record GetQueryDetailQuery : IRequest<QueryDetailsData>, IProjectAuthorizedRequest
{
    public required int QueryId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Query(QueryId), ProjectAction.Read)
    ];
}
