using MediatR;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.QueryVersions;

internal sealed class GetQueryVersionDetailHandler(IQueryVersionService versionService)
    : IRequestHandler<GetQueryVersionDetailQuery, QueryVersionDetail?>
{
    public async Task<QueryVersionDetail?> Handle(GetQueryVersionDetailQuery request, CancellationToken cancellationToken)
    {
        return await versionService.GetVersionDetailAsync(request.VersionId, cancellationToken);
    }
}

public record GetQueryVersionDetailQuery : IRequest<QueryVersionDetail?>, IProjectAuthorizedRequest
{
    public required int VersionId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.QueryVersion(VersionId), ProjectAction.Read)
    ];
}
