using MediatR;
using Semantico.Core.Models.Queries;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.QueryVersions;

internal sealed class GetQueryVersionsHandler(IQueryVersionService versionService)
    : IRequestHandler<GetQueryVersionsQuery, List<QueryVersionSummary>>
{
    public async Task<List<QueryVersionSummary>> Handle(GetQueryVersionsQuery request, CancellationToken cancellationToken)
    {
        return await versionService.GetVersionsAsync(request.QueryId, cancellationToken);
    }
}

public record GetQueryVersionsQuery : IRequest<List<QueryVersionSummary>>
{
    public required int QueryId { get; init; }
}
