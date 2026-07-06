using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskResultHistoryHandler(ITaskService taskService)
    : IRequestHandler<GetTaskResultHistoryQuery, TaskResultHistoryResult>
{
    public async Task<TaskResultHistoryResult> Handle(GetTaskResultHistoryQuery request, CancellationToken cancellationToken)
    {
        var history = await taskService.GetResultCountHistory(request.TaskId, cancellationToken);

        var items = history
            .Select(x => new TaskResultHistoryItem(x.Date, x.ResultCount))
            .ToList();

        return new TaskResultHistoryResult(request.TaskId, items);
    }
}

public record GetTaskResultHistoryQuery(int TaskId) : IRequest<TaskResultHistoryResult>;

public record TaskResultHistoryResult(int TaskId, IReadOnlyList<TaskResultHistoryItem> Points);

public record TaskResultHistoryItem(DateTime SampledAt, int ResultCount);
