using MediatR;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Handlers.Projects;

namespace Beacon.AI.Handlers.Projects;

internal sealed class GetRepositoryScanHandler(IBackgroundTaskService backgroundTasks)
    : IRequestHandler<GetRepositoryScanQuery, BackgroundTaskStatus?>
{
    public Task<BackgroundTaskStatus?> Handle(GetRepositoryScanQuery request, CancellationToken cancellationToken)
        => backgroundTasks.GetStatusAsync(BackgroundTaskKind.RepositoryScan, request.ProjectId, request.TaskId, cancellationToken);
}
