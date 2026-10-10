using MediatR;
using Beacon.Core.Services;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.QueryVersions;

internal sealed class RestoreQueryVersionHandler(IQueryVersionService versionService)
    : IRequestHandler<RestoreQueryVersionCommand, int>
{
    public async Task<int> Handle(RestoreQueryVersionCommand request, CancellationToken cancellationToken)
    {
        return await versionService.RestoreVersionAsync(request.VersionId, request.UserId, cancellationToken);
    }
}

public record RestoreQueryVersionCommand : IRequest<int>, IProjectAuthorizedRequest
{
    public required int VersionId { get; init; }
    [ProjectScopeExempt("the caller, set by the endpoint; not a project resource")]
    public string? UserId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.QueryVersion(VersionId), ProjectAction.Write)
    ];
}
