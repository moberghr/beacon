using Beacon.Core.Authorization;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskResultHistoryHandler(
    ITaskService taskService,
    IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetTaskResultHistoryQuery, TaskResultHistoryResult>
{
    public async Task<TaskResultHistoryResult> Handle(GetTaskResultHistoryQuery request, CancellationToken cancellationToken)
    {
        // The points are the result counts of the stored runs the caller may read (StoredRunAccess).
        var history = await taskService.GetResultCountHistory(
            request.TaskId,
            await StoredRunAccess.ScopeOfAsync(projectAccess, cancellationToken),
            cancellationToken);

        var items = history
            .Select(x => new TaskResultHistoryItem(x.Date, x.ResultCount))
            .ToList();

        return new TaskResultHistoryResult(request.TaskId, items);
    }
}

public record GetTaskResultHistoryQuery(int TaskId) : IRequest<TaskResultHistoryResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Task(TaskId), ProjectAction.Read)
    ];
}

public record TaskResultHistoryResult(int TaskId, IReadOnlyList<TaskResultHistoryItem> Points);

public record TaskResultHistoryItem(DateTime SampledAt, int ResultCount);
