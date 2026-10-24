using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data.Enums;
using MediatR;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourceMetadataRefreshHandler(IBackgroundTaskService backgroundTasks)
    : IRequestHandler<GetDataSourceMetadataRefreshQuery, BackgroundTaskStatus?>
{
    public Task<BackgroundTaskStatus?> Handle(GetDataSourceMetadataRefreshQuery request, CancellationToken cancellationToken)
        => backgroundTasks.GetStatusAsync(BackgroundTaskKind.MetadataRefresh, request.DataSourceId, request.TaskId, cancellationToken);
}

/// <summary>
/// The state of a background metadata refresh of the data source (<see cref="StartDataSourceMetadataRefreshCommand"/>), or
/// null when the data source has no refresh task with that id. Read with the check that starts one.
/// </summary>
public record GetDataSourceMetadataRefreshQuery(
    int DataSourceId,
    [property: ProjectScopeExempt("read only together with its data source: a task of another source is not found")] int TaskId)
    : IRequest<BackgroundTaskStatus?>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.SharedSource(ProjectResource.DataSource(DataSourceId), ProjectAction.Manage)
    ];
}
