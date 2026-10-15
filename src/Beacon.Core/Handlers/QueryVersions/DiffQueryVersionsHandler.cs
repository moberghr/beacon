using MediatR;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.QueryVersions;

internal sealed class DiffQueryVersionsHandler(IQueryVersionService versionService)
    : IRequestHandler<DiffQueryVersionsQuery, QueryVersionDiff>
{
    public async Task<QueryVersionDiff> Handle(DiffQueryVersionsQuery request, CancellationToken cancellationToken)
    {
        return await versionService.DiffVersionsAsync(request.VersionIdA, request.VersionIdB, cancellationToken);
    }
}

public record DiffQueryVersionsQuery : IRequest<QueryVersionDiff>, IProjectAuthorizedRequest
{
    public required int VersionIdA { get; init; }
    public required int VersionIdB { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.QueryVersion(VersionIdA), ProjectAction.Read),
        new(ProjectResource.QueryVersion(VersionIdB), ProjectAction.Read)
    ];
}
