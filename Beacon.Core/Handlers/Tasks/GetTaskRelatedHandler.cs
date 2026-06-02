using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskRelatedHandler(ITaskService taskService)
    : IRequestHandler<GetTaskRelatedQuery, TaskRelatedResult>
{
    public async Task<TaskRelatedResult> Handle(GetTaskRelatedQuery request, CancellationToken cancellationToken)
    {
        var related = await taskService.GetRelatedTasks(request.TaskId, cancellationToken);

        var items = related
            .Select(x =>
                new TaskRelatedItem(
                    x.Id,
                    x.CreatedAt,
                    x.LatestResultCount,
                    x.Resolved,
                    x.ResolvedAt))
            .ToList();

        return new TaskRelatedResult(request.TaskId, items);
    }
}

public record GetTaskRelatedQuery(int TaskId) : IRequest<TaskRelatedResult>;

public record TaskRelatedResult(int TaskId, IReadOnlyList<TaskRelatedItem> Related);

public record TaskRelatedItem(int Id, DateTime CreatedAt, int LatestResultCount, bool Resolved, DateTime? ResolvedAt);
