using Beacon.Core.Authorization;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskExecutionsHandler(
    ITaskService taskService,
    IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetTaskExecutionsQuery, TaskExecutionsResult>
{
    public async Task<TaskExecutionsResult> Handle(GetTaskExecutionsQuery request, CancellationToken cancellationToken)
    {
        // The executions are the stored runs the caller may read (StoredRunAccess).
        var executions = await taskService.GetTaskExecutionHistory(
            request.TaskId,
            await StoredRunAccess.ScopeOfAsync(projectAccess, cancellationToken),
            cancellationToken);

        var items = executions
            .Select(x =>
                new TaskExecutionItem(
                    x.Id,
                    x.ExecutedAt,
                    x.ExecutionTimeMs,
                    x.ResultCount,
                    x.Status.ToString()))
            .ToList();

        return new TaskExecutionsResult(request.TaskId, items);
    }
}

public record GetTaskExecutionsQuery(int TaskId) : IRequest<TaskExecutionsResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Task(TaskId), ProjectAction.Read)
    ];
}

public record TaskExecutionsResult(int TaskId, IReadOnlyList<TaskExecutionItem> Executions);

public record TaskExecutionItem(int Id, DateTime ExecutedAt, double DurationMs, int RowCount, string Status);
